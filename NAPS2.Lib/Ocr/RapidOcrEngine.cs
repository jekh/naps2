using System.Collections.Immutable;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using NAPS2.Scan;
using RapidOcrNet;
using SkiaSharp;

namespace NAPS2.Ocr;

public class RapidOcrEngine : IOcrEngine, IDisposable
{
    private readonly string? _modelPath;
    private readonly SessionOptions? _sessionOptions;
    private RapidOcr? _engine;
    private readonly object _initLock = new();

    public RapidOcrEngine(string? modelPath = null, SessionOptions? sessionOptions = null)
    {
        _modelPath = modelPath;
        _sessionOptions = sessionOptions;
    }

    public event EventHandler<OcrErrorEventArgs>? OcrError;
    public event EventHandler? OcrTimeout;

    public async Task<OcrResult?> ProcessImage(
        ScanningContext scanningContext,
        string imagePath,
        OcrParams ocrParams,
        CancellationToken cancelToken)
    {
        var logger = scanningContext.Logger;
        try
        {
            EnsureInitialized();

            if (ocrParams.Mode.HasFlag(OcrMode.WithPreProcess))
            {
                PreProcessImage(scanningContext, imagePath);
            }

            cancelToken.ThrowIfCancellationRequested();

            using var bitmap = SKBitmap.Decode(imagePath);
            if (bitmap == null)
            {
                logger.LogError("Failed to decode image for OCR: {Path}", imagePath);
                return null;
            }

            var rapidResult = await Task.Run(
                () => _engine!.Detect(bitmap, RapidOcrOptions.Default),
                cancelToken);

            if (rapidResult?.TextBlocks == null || rapidResult.TextBlocks.Length == 0)
            {
                return new OcrResult(
                    (0, 0, bitmap.Width, bitmap.Height),
                    ImmutableList<OcrResultElement>.Empty,
                    ImmutableList<OcrResultElement>.Empty);
            }

            return ConvertResult(rapidResult, bitmap.Width, bitmap.Height,
                ocrParams.LanguageCode ?? "eng");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error running RapidOCR");
            OcrError?.Invoke(this, new OcrErrorEventArgs(e));
            return null;
        }
    }

    private void EnsureInitialized()
    {
        if (_engine != null) return;
        lock (_initLock)
        {
            if (_engine != null) return;
            _engine = new RapidOcr();
            if (_modelPath != null)
            {
                var detPath = Path.Combine(_modelPath, RapidOcr.DefaultDetModelPath);
                var clsPath = Path.Combine(_modelPath, RapidOcr.DefaultClsModelPath);
                var recPath = Path.Combine(_modelPath, RapidOcr.DefaultRecModelPath);
                var keysPath = Path.Combine(_modelPath, RapidOcr.DefaultKeysFilePath);
                if (_sessionOptions != null)
                    _engine.InitModels(detPath, clsPath, recPath, keysPath, _sessionOptions);
                else
                    _engine.InitModels(detPath, clsPath, recPath, keysPath);
            }
            else if (_sessionOptions != null)
            {
                _engine.InitModels(_sessionOptions);
            }
            else
            {
                _engine.InitModels();
            }
        }
    }

    private static OcrResult ConvertResult(
        RapidOcrNet.OcrResult rapidResult,
        int pageWidth, int pageHeight,
        string languageCode)
    {
        var words = new List<OcrResultElement>();
        var lines = new List<OcrResultElement>();

        foreach (var block in rapidResult.TextBlocks)
        {
            var text = block.GetText();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var bounds = BoundsFromPoints(block.BoxPoints);
            int fontSize = Math.Max(8, (int)(bounds.h * 0.75));
            int baseline = bounds.y + bounds.h;

            var wordElement = new OcrResultElement(
                text,
                languageCode,
                RightToLeft: false,
                bounds,
                baseline,
                fontSize,
                ImmutableList<OcrResultElement>.Empty);

            words.Add(wordElement);
            lines.Add(wordElement with
            {
                Children = ImmutableList.Create(wordElement)
            });
        }

        return new OcrResult(
            (0, 0, pageWidth, pageHeight),
            words.ToImmutableList(),
            lines.ToImmutableList());
    }

    private static (int x, int y, int w, int h) BoundsFromPoints(SKPointI[] points)
    {
        int minX = points.Min(p => p.X);
        int minY = points.Min(p => p.Y);
        int maxX = points.Max(p => p.X);
        int maxY = points.Max(p => p.Y);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private static void PreProcessImage(ScanningContext scanningContext, string imagePath)
    {
        IMemoryImage? image = null;
        try
        {
            image = scanningContext.ImageContext.Load(imagePath);
            image = image.PerformTransform(new CorrectionTransform(CorrectionMode.Document));
            image.Save(imagePath);
        }
        finally
        {
            image?.Dispose();
        }
    }

    public void Dispose()
    {
        _engine?.Dispose();
    }
}
