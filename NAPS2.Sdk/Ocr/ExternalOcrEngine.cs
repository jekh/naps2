using System.Threading;
using System.Xml;
using Microsoft.Extensions.Logging;
using NAPS2.Scan;

namespace NAPS2.Ocr;

public class ExternalOcrEngine : IOcrEngine
{
    private readonly string _executablePath;
    private readonly string? _argumentsTemplate;

    public ExternalOcrEngine(string executablePath, string? argumentsTemplate = null)
    {
        _executablePath = executablePath;
        _argumentsTemplate = argumentsTemplate;
    }

    public event EventHandler<OcrErrorEventArgs>? OcrError;

    public event EventHandler? OcrTimeout;

    public async Task<OcrResult?> ProcessImage(ScanningContext scanningContext, string imagePath, OcrParams ocrParams,
        CancellationToken cancelToken)
    {
        var logger = scanningContext.Logger;
        string tempHocrFilePath = Path.Combine(scanningContext.TempFolderPath, Path.GetRandomFileName());
        string tempHocrFilePathWithExt = tempHocrFilePath + ".hocr";
        try
        {
            if (ocrParams.Mode.HasFlag(OcrMode.WithPreProcess))
            {
                PreProcessImage(scanningContext, imagePath);
            }

            var arguments = BuildArguments(imagePath, tempHocrFilePath, ocrParams);

            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var process = Process.Start(startInfo);
            if (process == null)
            {
                logger.LogError("Couldn't start external OCR process.");
                return null;
            }
            process.PriorityClass = ProcessPriorityClass.BelowNormal;

            var waitTasks = new List<Task>
            {
                process.WaitForExitAsync(),
                cancelToken.WaitHandle.WaitOneAsync()
            };
            var timeout = (int) (ocrParams.TimeoutInSeconds * 1000);
            if (timeout > 0)
            {
                waitTasks.Add(Task.Delay(timeout));
            }
            await Task.WhenAny(waitTasks);

            if (!process.HasExited)
            {
                if (!cancelToken.IsCancellationRequested)
                {
                    logger.LogError("External OCR process timed out.");
                    OcrTimeout?.Invoke(this, EventArgs.Empty);
                }
                try
                {
                    process.Kill();
                    Thread.Sleep(200);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error killing external OCR process");
                }
                return null;
            }

            XDocument hocrDocument = XDocument.Load(tempHocrFilePathWithExt);
            return HocrParser.Parse(hocrDocument);
        }
        catch (XmlException e)
        {
            logger.LogError(e, "Error parsing external OCR output");
            return null;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error running external OCR");
            OcrError?.Invoke(this, new OcrErrorEventArgs(e));
            return null;
        }
        finally
        {
            try
            {
                File.Delete(tempHocrFilePathWithExt);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error cleaning up OCR temp files");
            }
        }
    }

    private string BuildArguments(string imagePath, string hocrOutputPath, OcrParams ocrParams)
    {
        var template = _argumentsTemplate ?? "\"{input}\" \"{output}\"";
        return template
            .Replace("{input}", imagePath)
            .Replace("{output}", hocrOutputPath)
            .Replace("{lang}", ocrParams.LanguageCode ?? "eng")
            .Replace("{timeout}", ((int) (ocrParams.TimeoutInSeconds * 1000)).ToString());
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
}
