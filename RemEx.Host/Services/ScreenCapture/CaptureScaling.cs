namespace Remex.Host.Services.ScreenCapture;

/// <summary>
/// Single source of truth for the scaled output dimensions of a captured frame.
///
/// The H.264 encoder is started with a fixed input size (<c>-s WxH</c>) and consumes raw BGRA
/// frames of exactly <c>W * H * 4</c> bytes from stdin. If the capture path produces a frame of a
/// different size — even by one pixel — the rawvideo pipe desyncs and the encoder emits zero
/// frames (black screen). Previously the encoder, the GDI scaler, and the DXGI scaler each
/// computed dimensions slightly differently (even-alignment vs none, and different "skip scaling"
/// thresholds), so any scale that produced an odd dimension broke H.264.
///
/// All capture sites (encoder init, GDI <c>ScaleBitmap</c>, DXGI <c>EncodeToRawBgra</c>) MUST derive
/// their dimensions from <see cref="ScaledEven"/> so the delivered buffer always matches the encoder.
/// Dimensions are floored to an even value because most H.264 encoders require even width/height.
/// </summary>
internal static class CaptureScaling
{
    /// <summary>
    /// Returns the scaled, even-aligned size of a single dimension (width or height).
    /// Always at least 2 (the minimum valid even H.264 dimension).
    /// </summary>
    public static int ScaledEven(int dimension, double scale)
    {
        var scaled = (int)(dimension * scale);
        scaled = (scaled / 2) * 2;
        return System.Math.Max(2, scaled);
    }
}
