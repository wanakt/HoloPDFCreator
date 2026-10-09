namespace HoloPDFCreator.Services;

/// <summary>
/// OCR results of a document, page-indexed. Lets the PDF reader read them on save,
/// snapshot them for undo, and move them along when pages are reordered or deleted.
/// <para>
/// <c>docKey</c> is the document's own file path, not the temp file the reader renders
/// from: that temp path changes on every page edit, which used to lose the results.
/// </para>
/// </summary>
public interface IPageOcrStore
{
    /// <summary>A copy of the results with image size and page geometry; null when there are none.</summary>
    IReadOnlyDictionary<int, OcrPageData>? GetOcrData(string docKey);

    /// <summary>Replaces the results of <paramref name="docKey"/>; ignored for another document.</summary>
    void SetOcrData(string docKey, IReadOnlyDictionary<int, OcrPageData>? data);
}
