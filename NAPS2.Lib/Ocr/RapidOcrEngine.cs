using System.Collections.Immutable;
using System.Diagnostics;
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
    private readonly string _gpuBackend;
    private RapidOcr? _engine;
    private readonly object _initLock = new();
    private string? _activeProvider;

    public RapidOcrEngine(string? modelPath = null, string gpuBackend = "auto")
    {
        _modelPath = modelPath;
        _gpuBackend = gpuBackend.ToLowerInvariant();
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
            var initSw = Stopwatch.StartNew();
            EnsureInitialized(logger);
            initSw.Stop();

            if (_activeProvider != null)
            {
                logger.LogDebug(
                    "RapidOCR engine ready ({ElapsedMs}ms). Provider: {Provider}. Models: {ModelPath}",
                    initSw.ElapsedMilliseconds, _activeProvider, _modelPath ?? "(bundled)");
                _activeProvider = null;
            }

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

            if (rapidResult != null)
            {
                logger.LogDebug(
                    "RapidOCR completed: {TextBlocks} text blocks, detect={DetectMs:F0}ms, total={TotalMs:F0}ms",
                    rapidResult.TextBlocks?.Length ?? 0,
                    rapidResult.DetectTime,
                    rapidResult.DbNetTime + (rapidResult.TextBlocks?.Sum(b => b.BlockTime) ?? 0));
            }

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

    internal static SessionOptions CreateSessionOptions(string gpuBackend, ILogger? logger = null)
    {
        var sessionOptions = new SessionOptions();
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED;

        string[] available;
        try
        {
            available = OrtEnv.Instance().GetAvailableProviders();
            logger?.LogDebug("ONNX Runtime available providers: {Providers}", string.Join(", ", available));
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Failed to query available ONNX Runtime providers, falling back to CPU");
            return sessionOptions;
        }

        var requested = gpuBackend;
        if (requested == "auto")
        {
            if (OperatingSystem.IsWindows() && available.Contains("DmlExecutionProvider"))
                requested = "directml";
            else if (available.Contains("CoreMLExecutionProvider"))
                requested = "coreml";
            else if (available.Contains("CUDAExecutionProvider"))
                requested = "cuda";
            else if (available.Contains("ROCmExecutionProvider"))
                requested = "rocm";
            else if (available.Contains("OpenVINOExecutionProvider"))
                requested = "openvino";
            else
                requested = "cpu";
        }

        try
        {
            switch (requested)
            {
                case "directml":
                    sessionOptions.AppendExecutionProvider_DML(0);
                    logger?.LogInformation("RapidOCR using DirectML GPU acceleration");
                    break;
                case "coreml":
                    sessionOptions.AppendExecutionProvider_CoreML(
                        CoreMLFlags.COREML_FLAG_ENABLE_ON_SUBGRAPH);
                    logger?.LogInformation("RapidOCR using CoreML GPU acceleration");
                    break;
                case "cuda":
                    sessionOptions.AppendExecutionProvider_CUDA(0);
                    logger?.LogInformation("RapidOCR using CUDA GPU acceleration");
                    break;
                case "rocm":
                    sessionOptions.AppendExecutionProvider_ROCm(0);
                    logger?.LogInformation("RapidOCR using ROCm GPU acceleration");
                    break;
                case "openvino":
                    sessionOptions.AppendExecutionProvider_OpenVINO("GPU");
                    logger?.LogInformation("RapidOCR using OpenVINO GPU acceleration");
                    break;
                default:
                    logger?.LogInformation("RapidOCR using CPU execution provider");
                    break;
            }
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "{Provider} not available, falling back to CPU", requested);
        }

        sessionOptions.AppendExecutionProvider_CPU();
        return sessionOptions;
    }

    private void EnsureInitialized(ILogger logger)
    {
        if (_engine != null) return;
        lock (_initLock)
        {
            if (_engine != null) return;

            var sessionOptions = CreateSessionOptions(_gpuBackend, logger);
            _engine = new RapidOcr();

            if (_modelPath != null)
            {
                var detPath = Path.Combine(_modelPath, RapidOcr.DefaultDetModelPath);
                var clsPath = Path.Combine(_modelPath, RapidOcr.DefaultClsModelPath);
                var recPath = Path.Combine(_modelPath, RapidOcr.DefaultRecModelPath);
                var keysPath = Path.Combine(_modelPath, RapidOcr.DefaultKeysFilePath);
                _engine.InitModels(detPath, clsPath, recPath, keysPath, sessionOptions);
            }
            else
            {
                _engine.InitModels(sessionOptions);
            }

            _activeProvider = _gpuBackend;
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
