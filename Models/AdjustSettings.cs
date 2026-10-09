namespace HoloPDFCreator.Models;

/// <summary>Image adjustment of one page (brightness, contrast, stroke, unsharp mask, auto level).</summary>
public record AdjustSettings(double Brightness, double Contrast, double Stroke,
                             double UsmAmount, double UsmRadius, double UsmThreshold, bool AutoLevel);
