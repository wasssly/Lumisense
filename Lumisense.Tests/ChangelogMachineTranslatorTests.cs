using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace Lumisense.Tests;

// Автоперевод журнала изменений: разбор ответа, проверка перевода, кэш на диске и поведение при отказах сервиса. Сеть не используется.
public sealed class ChangelogMachineTranslatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumisense-changelog-tests-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_directory, "changelog-translations.json");

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); }
        catch (IOException) { /* временная папка теста, остатки не критичны */ }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Один поток: порядок запросов и число обращений после отказа сервиса предсказуемы.
    private ChangelogMachineTranslator Create(StubHandler handler, int maxParallel = 1) =>
        new(() => new HttpClient(handler), CachePath, TimeSpan.Zero, maxParallel);

    [Fact]
    public void ParseResponse_JoinsSentenceSegments()
    {
        const string json = """[[["Added a button. ","Добавлена кнопка. ",null,null,10],["Fixed a crash.","Исправлен сбой.",null,null,10]],null,"ru"]""";

        Assert.Equal("Added a button. Fixed a crash.", ChangelogMachineTranslator.ParseResponse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[[[]]]")]
    public void ParseResponse_ReturnsNullForUnexpectedShapes(string json) =>
        Assert.Null(ChangelogMachineTranslator.ParseResponse(json));

    [Theory]
    [InlineData("Добавлена кнопка", "Added a button", true)]
    [InlineData("Добавлена кнопка", "Добавлена кнопка", false)]
    [InlineData("Добавлена кнопка", "", false)]
    [InlineData("Добавлена кнопка", "Добавлена button", false)]
    [InlineData("Добавлена кнопка", "12345", false)]
    [InlineData("Да", "This is a very long translation that is far larger than the tiny source text could ever justify here", false)]
    public void IsPlausible_AcceptsOnlySaneEnglishTranslations(string source, string translated, bool expected) =>
        Assert.Equal(expected, ChangelogMachineTranslator.IsPlausible(source, translated));

    [Theory]
    [InlineData("Исправлена ошибка", true)]
    [InlineData("Already English", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTranslatable_RequiresRussianText(string? text, bool expected) =>
        Assert.Equal(expected, ChangelogMachineTranslator.IsTranslatable(text));

    [Fact]
    public void IsTranslatable_RejectsVeryLongText() =>
        Assert.False(ChangelogMachineTranslator.IsTranslatable(new string('я', ChangelogMachineTranslator.MaxSourceLength + 1)));

    [Fact]
    public async Task TranslateAsync_TranslatesStoresAndDoesNotAskTwice()
    {
        var handler = new StubHandler(request =>
        {
            Assert.StartsWith(ChangelogMachineTranslator.Endpoint, request.RequestUri!.ToString());
            Assert.Contains("sl=ru", request.RequestUri.Query);
            Assert.Contains("tl=en", request.RequestUri.Query);
            return Json("""[[["Added a button","Добавлена кнопка",null,null,10]],null,"ru"]""");
        });
        ChangelogMachineTranslator translator = Create(handler);
        int progress = 0;

        int first = await translator.TranslateAsync(["Добавлена кнопка"], () => progress++, CancellationToken.None);
        int second = await translator.TranslateAsync(["Добавлена кнопка"], null, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, handler.Requests);
        Assert.True(progress >= 1);
        Assert.True(translator.TryGetCached("Добавлена кнопка", out string translated));
        Assert.Equal("Added a button", translated);
    }

    [Fact]
    public async Task TranslateAsync_TranslatesSeveralItemsInParallel_WithoutLosingAny()
    {
        string[] sources = Enumerable.Range(1, 25).Select(number => $"Пункт номер {number}").ToArray();
        var handler = new StubHandler(request =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            string number = query[(query.LastIndexOf(' ') + 1)..];
            return Json($$"""[[["Item number {{number}}","x",null,null,10]],null,"ru"]""");
        });
        ChangelogMachineTranslator translator = Create(handler, maxParallel: 4);

        int translated = await translator.TranslateAsync(sources, null, CancellationToken.None);

        Assert.Equal(25, translated);
        Assert.Equal(25, handler.Requests);
        Assert.True(translator.TryGetCached("Пункт номер 17", out string text));
        Assert.Equal("Item number 17", text);

        // Кэш сохранён целиком и читается новым экземпляром.
        ChangelogMachineTranslator reloaded = Create(new StubHandler(_ => throw new InvalidOperationException("запрос не нужен")));
        Assert.All(sources, source => Assert.True(reloaded.TryGetCached(source, out _), source));
    }

    [Fact]
    public async Task TranslateAsync_ParallelRun_StopsEveryoneAfterRefusal()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        ChangelogMachineTranslator translator = Create(handler, maxParallel: 4);

        int translated = await translator.TranslateAsync(Enumerable.Range(1, 100).Select(number => $"Пункт {number}"), null, CancellationToken.None);

        Assert.Equal(0, translated);
        Assert.True(handler.Requests <= 8, $"после отказа не должно уходить много запросов, ушло {handler.Requests}");
    }

    [Fact]
    public async Task TranslateAsync_PersistsCacheAcrossInstances()
    {
        var first = Create(new StubHandler(_ => Json("""[[["Fixed a crash","Исправлен сбой",null,null,10]],null,"ru"]""")));
        await first.TranslateAsync(["Исправлен сбой"], null, CancellationToken.None);

        var handler = new StubHandler(_ => throw new InvalidOperationException("запрос не нужен"));
        ChangelogMachineTranslator second = Create(handler);

        Assert.True(second.TryGetCached("Исправлен сбой", out string translated));
        Assert.Equal("Fixed a crash", translated);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task TranslateAsync_IgnoresEnglishTextAndRejectedTranslations()
    {
        var handler = new StubHandler(_ => Json("""[[["Исправлен сбой","Исправлен сбой",null,null,10]],null,"ru"]"""));
        ChangelogMachineTranslator translator = Create(handler);

        int translated = await translator.TranslateAsync(["Already English", "Исправлен сбой"], null, CancellationToken.None);

        Assert.Equal(0, translated);
        Assert.Equal(1, handler.Requests); // английский текст не запрашивается, отклонённый перевод не сохраняется
        Assert.False(translator.TryGetCached("Исправлен сбой", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TranslateAsync_StopsAndPausesAfterServiceRefusal(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        ChangelogMachineTranslator translator = Create(handler);

        int translated = await translator.TranslateAsync(["Первый пункт", "Второй пункт"], null, CancellationToken.None);
        int later = await translator.TranslateAsync(["Третий пункт"], null, CancellationToken.None);

        Assert.Equal(0, translated);
        Assert.Equal(0, later);
        Assert.Equal(1, handler.Requests); // после отказа остальные пункты и следующие прогоны не запрашиваются
    }

    [Fact]
    public async Task TranslateAsync_SkipsAnItemThatTheServiceRejectsWithClientError()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains(Uri.EscapeDataString("Плохой"))
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : Json("""[[["Good one","Хороший",null,null,10]],null,"ru"]"""));
        ChangelogMachineTranslator translator = Create(handler);

        int translated = await translator.TranslateAsync(["Плохой пункт", "Хороший пункт"], null, CancellationToken.None);

        Assert.Equal(1, translated);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task TranslateAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ChangelogMachineTranslator translator = Create(new StubHandler(_ => Json("[]")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => translator.TranslateAsync(["Пункт"], null, cts.Token));
    }

    [Fact]
    public void Cache_IgnoresCorruptFileAndImplausibleEntries()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CachePath, "{ это не json");
        ChangelogMachineTranslator broken = Create(new StubHandler(_ => Json("[]")));
        Assert.False(broken.TryGetCached("Пункт", out _));

        File.WriteAllText(CachePath, """{"Пункт":"Пункт","Другой пункт":"Another item"}""");
        ChangelogMachineTranslator mixed = Create(new StubHandler(_ => Json("[]")));
        Assert.False(mixed.TryGetCached("Пункт", out _));
        Assert.True(mixed.TryGetCached("Другой пункт", out string translated));
        Assert.Equal("Another item", translated);
    }
}

// Готовых английских переводов в приложении нет: без автоперевода журнал остаётся русским.
public sealed class ChangelogTranslationCatalogBehaviorTests
{
    [Fact]
    public void Translate_LeavesRussianUnchangedForRussianInterface() =>
        Assert.Equal("Автопереход к следующему треку", ChangelogTranslationCatalog.Translate("Автопереход к следующему треку", false));

    [Fact]
    public void Translate_LeavesEmptyTextUnchanged()
    {
        Assert.Equal("", ChangelogTranslationCatalog.Translate("", true));
    }

    [Fact]
    public void NeedsMachineTranslation_IsFalseForEnglishText() =>
        Assert.False(ChangelogTranslationCatalog.NeedsMachineTranslation("Already English"));
}
