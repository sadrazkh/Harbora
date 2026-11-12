using System.Globalization;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Harbora.Web.Controllers;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Round nine, item three: an upload as large as the page says goes through, and one over it is
/// refused in a sentence in both languages instead of a bare 400 or 413. A real two-gigabyte upload is
/// not exercised here; what is held is that every limit an import passes through agrees with the one
/// the page states, and that the refusal is what answers above it, whichever limit noticed first.
/// </summary>
public class UploadLimitTests
{
    public static TheoryData<string, long, string> Imports => new()
    {
        { nameof(DatabasesController.Import), DatabasesController.MaxImportBytes, nameof(DatabasesController.Details) },
        { nameof(DatabasesController.ImportDatabase), DatabasesController.MaxImportBytes, nameof(DatabasesController.ConfirmImport) },
    };

    // ---- the limits agree ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Imports))]
    public void Every_limit_a_database_import_passes_through_lets_the_stated_size_in(string action, long stated, string back) =>
        LimitsAgree(typeof(DatabasesController).GetMethod(action)!, stated, back);

    [Fact]
    public void An_uploaded_backup_s_limits_agree_with_its_stated_size_too() =>
        LimitsAgree(typeof(BackupsController).GetMethod(nameof(BackupsController.Upload))!,
            BackupsController.ImportSizeLimitBytes, nameof(BackupsController.Index));

    private static void LimitsAgree(MethodInfo method, long stated, string back)
    {
        new FormOptions().MultipartBodyLengthLimit.Should().BeLessThan(stated,
            "this is the default that refused every import over 128 MB, and why the form limit has to be set");

        var body = method.GetCustomAttribute<RequestSizeLimitAttribute>();
        body.Should().NotBeNull();
        BodyLimitOf(body!).Should().Be(stated + UploadLimit.FormOverheadBytes);

        var form = method.GetCustomAttribute<RequestFormLimitsAttribute>();
        form.Should().NotBeNull("without it the form reader's 128 MB default answers first");
        form!.MultipartBodyLengthLimit.Should().Be(stated + UploadLimit.FormOverheadBytes);

        var refusal = method.GetCustomAttribute<RefuseOversizedUploadAttribute>();
        refusal.Should().NotBeNull("without it anything over the limit is a bare 400 or 413");
        refusal!.MaxFileBytes.Should().Be(stated);
        refusal.RedirectToAction.Should().Be(back);

        // After both limits are applied, before the antiforgery check reads the form.
        refusal.Order.Should().BeGreaterThan(body!.Order).And.BeGreaterThan(form.Order);
        refusal.Order.Should().BeLessThan(new ValidateAntiForgeryTokenAttribute().Order);
        method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
    }

    private static long? BodyLimitOf(RequestSizeLimitAttribute attribute) =>
        ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)attribute).MaxRequestBodySize;

    // ---- the refusal -----------------------------------------------------------------------------

    [Fact]
    public async Task A_body_declared_longer_than_the_ceiling_is_refused_in_a_sentence_without_being_read()
    {
        var (context, tempData) = Context(id: Guid.CreateVersion7());
        context.HttpContext.Request.ContentLength = DatabasesController.MaxImportBytes + UploadLimit.FormOverheadBytes + 1;
        context.HttpContext.Request.Body = new ThrowingStream(new InvalidOperationException("the body was read"));

        await Guard().OnAuthorizationAsync(context);

        var redirect = context.Result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(DatabasesController.Details));
        redirect.ControllerName.Should().BeNull("the same controller");
        redirect.RouteValues!.Keys.Should().BeEquivalentTo(["id"]);
        tempData.Saved["Error"].Should().Be("This file is larger than 2 GB, the most this accepts. Nothing was read from it.");
    }

    [Fact]
    public async Task A_section_over_the_form_reader_s_limit_is_refused_the_same_way()
    {
        var (context, tempData) = Context(id: Guid.CreateVersion7(), databaseId: Guid.CreateVersion7());
        var request = context.HttpContext.Request;
        Multipart(request, fileBytes: 4096);
        request.ContentLength = null;
        context.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions { MultipartBodyLengthLimit = 1024 }));

        await new RefuseOversizedUploadAttribute(DatabasesController.MaxImportBytes, nameof(DatabasesController.ConfirmImport))
            .OnAuthorizationAsync(context);

        var redirect = context.Result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(DatabasesController.ConfirmImport));
        redirect.RouteValues!.Keys.Should().BeEquivalentTo(["id", "databaseId"]);
        tempData.Saved.Should().ContainKey("Error");
    }

    [Fact]
    public async Task A_body_the_server_stops_for_its_own_size_is_refused_the_same_way()
    {
        var (context, tempData) = Context(id: Guid.CreateVersion7());
        var request = context.HttpContext.Request;
        request.ContentType = "multipart/form-data; boundary=b";
        request.ContentLength = null;
        request.Body = new ThrowingStream(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        await Guard().OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<RedirectToActionResult>();
        tempData.Saved.Should().ContainKey("Error");
    }

    [Fact]
    public async Task A_Persian_reader_is_refused_in_Persian()
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fa-IR");
        try
        {
            var (context, tempData) = Context(id: Guid.CreateVersion7());
            context.HttpContext.Request.ContentLength = long.MaxValue;

            await Guard().OnAuthorizationAsync(context);

            tempData.Saved["Error"].Should().Be("این فایل از ۲ گیگابایت بزرگ‌تر است، یعنی بیشتر از سقفی که اینجا پذیرفته می‌شود. چیزی از آن خوانده نشد.");
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public async Task An_upload_within_the_ceiling_goes_on_with_its_form_already_read_for_the_action()
    {
        var (context, tempData) = Context(id: Guid.CreateVersion7());
        Multipart(context.HttpContext.Request, fileBytes: 4096);

        await Guard().OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
        tempData.Saved.Should().BeEmpty();
        context.HttpContext.Request.HasFormContentType.Should().BeTrue();
        (await context.HttpContext.Request.ReadFormAsync()).Files.Should().ContainSingle(f => f.Length == 4096);
    }

    [Fact]
    public async Task A_request_an_earlier_filter_already_answered_is_left_as_it_is()
    {
        var (context, tempData) = Context(id: Guid.CreateVersion7());
        context.HttpContext.Request.ContentLength = long.MaxValue;
        var challenge = new ChallengeResult();
        context.Result = challenge;

        await Guard().OnAuthorizationAsync(context);

        context.Result.Should().BeSameAs(challenge);
        tempData.Saved.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2L * 1024 * 1024 * 1024, false, "2 GB")]
    [InlineData(8L * 1024 * 1024 * 1024, true, "۸ گیگابایت")]
    [InlineData(512L * 1024 * 1024, false, "512 MB")]
    [InlineData(512L * 1024 * 1024, true, "۵۱۲ مگابایت")]
    public void The_ceiling_reads_the_way_a_person_says_it(long bytes, bool isFa, string said)
    {
        UploadLimit.Describe(bytes, isFa).Should().Be(said);
        UploadLimit.UpTo(bytes, isFa).Should().EndWith(said);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static RefuseOversizedUploadAttribute Guard() =>
        new(DatabasesController.MaxImportBytes, nameof(DatabasesController.Details));

    private static (AuthorizationFilterContext Context, RecordingTempData TempData) Context(Guid id, Guid? databaseId = null)
    {
        var tempData = new RecordingTempData();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ITempDataDictionaryFactory>(new TempDataDictionaryFactory(tempData))
                .BuildServiceProvider()
        };
        http.Request.Method = "POST";

        var route = new RouteData();
        route.Values["controller"] = "Databases";
        route.Values["action"] = "Import";
        route.Values["id"] = id;
        if (databaseId is { } database) route.Values["databaseId"] = database;

        var action = new ActionContext(http, route, new ActionDescriptor());
        return (new AuthorizationFilterContext(action, new List<IFilterMetadata>()), tempData);
    }

    private static void Multipart(HttpRequest request, int fileBytes)
    {
        const string boundary = "----harbora-test";
        var body = new MemoryStream();
        void Text(string s) => body.Write(Encoding.UTF8.GetBytes(s));
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"confirmName\"\r\n\r\norders\r\n");
        Text($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"dump.sql.gz\"\r\n" +
             "Content-Type: application/gzip\r\n\r\n");
        body.Write(new byte[fileBytes]);
        Text($"\r\n--{boundary}--\r\n");
        body.Position = 0;

        request.ContentType = $"multipart/form-data; boundary={boundary}";
        request.ContentLength = body.Length;
        request.Body = body;
    }

    /// <summary>Keeps what was saved, so a test sees what the redirect will carry.</summary>
    private sealed class RecordingTempData : ITempDataProvider
    {
        public Dictionary<string, object> Saved { get; } = [];

        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            foreach (var (key, value) in values) Saved[key] = value;
        }
    }

    private sealed class ThrowingStream(Exception toThrow) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw toThrow;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw toThrow;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw toThrow;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
