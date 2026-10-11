using System.Runtime.CompilerServices;

namespace SpacePreview.Tests;

/// <summary>
/// The suite renders PDFs in its own process, page after page, the way only
/// the short-lived helpers do in the app. pdfium's form-fill teardown faults
/// once enough pages have rendered, which killed the test host about one run
/// in six and left a partial "Passed!" behind. Like a helper, the host skips
/// the teardown and lets its exit reclaim the documents.
/// </summary>
internal static class TestPdfium
{
    [ModuleInitializer]
    internal static void Initialize() => SpacePreview.Preview.PdfPreviewRenderer.SkipDocumentTeardown = true;
}
