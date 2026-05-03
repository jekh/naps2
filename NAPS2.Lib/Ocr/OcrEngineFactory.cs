using NAPS2.Logging;
using NAPS2.Scan;

namespace NAPS2.Ocr;

public class OcrEngineFactory
{
    private readonly Naps2Config _config;
    private readonly TesseractLanguageManager _tesseractLanguageManager;
    private readonly ErrorOutput _errorOutput;

    public OcrEngineFactory(Naps2Config config, TesseractLanguageManager tesseractLanguageManager,
        ErrorOutput errorOutput)
    {
        _config = config;
        _tesseractLanguageManager = tesseractLanguageManager;
        _errorOutput = errorOutput;
    }

    public IOcrEngine Create()
    {
        var engineType = _config.Get(c => c.OcrEngineType);

        if (engineType == "External")
        {
            var path = _config.Get(c => c.OcrEnginePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    "OcrEngineType is 'External' but OcrEnginePath is not set.");
            }
            var extEngine = new ExternalOcrEngine(path, _config.Get(c => c.OcrEngineArgs));
            extEngine.OcrError += (_, args) => _errorOutput.DisplayError(SdkResources.OcrError, args.Exception);
            extEngine.OcrTimeout += (_, _) => _errorOutput.DisplayError(SdkResources.OcrTimeout);
            return extEngine;
        }

        if (engineType == "RapidOCR")
        {
            var modelPath = _config.Get(c => c.RapidOcrModelPath);
            var gpuBackend = _config.Get(c => c.RapidOcrGpuBackend);
            var rapidEngine = new RapidOcrEngine(
                string.IsNullOrWhiteSpace(modelPath) ? null : modelPath,
                string.IsNullOrWhiteSpace(gpuBackend) ? "auto" : gpuBackend);
            rapidEngine.OcrError += (_, args) => _errorOutput.DisplayError(SdkResources.OcrError, args.Exception);
            rapidEngine.OcrTimeout += (_, _) => _errorOutput.DisplayError(SdkResources.OcrTimeout);
            return rapidEngine;
        }

        var tessEngine = TesseractOcrEngine.BundledWithModes(_tesseractLanguageManager.TessdataBasePath);
        tessEngine.OcrError += (_, args) => _errorOutput.DisplayError(SdkResources.OcrError, args.Exception);
        tessEngine.OcrTimeout += (_, _) => _errorOutput.DisplayError(SdkResources.OcrTimeout);
        return tessEngine;
    }
}
