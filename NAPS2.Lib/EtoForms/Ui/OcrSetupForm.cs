using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using NAPS2.EtoForms.Layout;
using NAPS2.EtoForms.Widgets;
using NAPS2.Lang;
using NAPS2.Ocr;
using NAPS2.Scan;

namespace NAPS2.EtoForms.Ui;

public class OcrSetupForm : EtoDialogBase
{
    private readonly TesseractLanguageManager _tesseractLanguageManager;
    private readonly ScanningContext _scanningContext;
    private readonly OcrEngineFactory _ocrEngineFactory;

    private readonly CheckBox _enableOcr = C.CheckBox(UiStrings.MakePdfsSearchable);
    private readonly DropDown _engineType = C.DropDown();
    private readonly DropDown _ocrLang = C.DropDown();
    private readonly EnumDropDownWidget<LocalizedOcrMode> _ocrMode = new()
        { Items = [LocalizedOcrMode.Fast, LocalizedOcrMode.Best] };
    private readonly DropDown _gpuBackend = C.DropDown();
    private readonly CheckBox _ocrPreProcessing = C.CheckBox(UiStrings.OcrPreProcessing);
    private readonly CheckBox _ocrAfterScanning = C.CheckBox(
        TranslationMigrator.PickTranslated(
            UiStrings.ResourceManager,
            "RunOcrAfterScanning",
            "PreemptivelyOcrAfterScanning"));
    private readonly LinkButton _moreLanguages = C.Link(UiStrings.GetMoreLanguages);

    private readonly LayoutVisibility _tesseractVis = new(true);
    private readonly LayoutVisibility _rapidOcrVis = new(false);

    private string? _code;
    private string? _multiLangCode;
    private bool _suppressLangChangeEvent;

    public OcrSetupForm(Naps2Config config, TesseractLanguageManager tesseractLanguageManager,
        ScanningContext scanningContext, OcrEngineFactory ocrEngineFactory,
        IIconProvider iconProvider) : base(config)
    {
        Title = UiStrings.OcrSetupFormTitle;
        IconName = "text_small";

        _tesseractLanguageManager = tesseractLanguageManager;
        _scanningContext = scanningContext;
        _ocrEngineFactory = ocrEngineFactory;

        _engineType.Items.Add(new ListItem { Key = "Tesseract", Text = "Tesseract" });
        _engineType.Items.Add(new ListItem { Key = "RapidOCR", Text = "RapidOCR (PaddleOCR)" });
        _engineType.SelectedKey = Config.Get(c => c.OcrEngineType);
        if (_engineType.SelectedIndex == -1)
            _engineType.SelectedIndex = 0;
        _engineType.SelectedIndexChanged += EngineType_SelectedIndexChanged;

        _gpuBackend.Items.Add(new ListItem { Key = "auto", Text = UiStrings.OcrGpuAuto });
        _gpuBackend.Items.Add(new ListItem { Key = "cpu", Text = UiStrings.OcrGpuCpu });
        if (OperatingSystem.IsWindows())
            _gpuBackend.Items.Add(new ListItem { Key = "directml", Text = UiStrings.OcrGpuDirectML });
        if (OperatingSystem.IsMacOS())
            _gpuBackend.Items.Add(new ListItem { Key = "coreml", Text = UiStrings.OcrGpuCoreML });
        _gpuBackend.Items.Add(new ListItem { Key = "cuda", Text = UiStrings.OcrGpuCuda });
        if (OperatingSystem.IsLinux())
            _gpuBackend.Items.Add(new ListItem { Key = "rocm", Text = UiStrings.OcrGpuROCm });
        _gpuBackend.Items.Add(new ListItem { Key = "openvino", Text = UiStrings.OcrGpuOpenVINO });
        _gpuBackend.SelectedKey = Config.Get(c => c.RapidOcrGpuBackend);
        if (_gpuBackend.SelectedIndex == -1)
            _gpuBackend.SelectedIndex = 0;

        _enableOcr.CheckedChanged += EnableOcr_CheckedChanged;
        _moreLanguages.Click += MoreLanguages_Click;

        var configOcrMode = Config.Get(c => c.OcrMode);
        if (configOcrMode == LocalizedOcrMode.Legacy)
        {
            configOcrMode = LocalizedOcrMode.Fast;
        }

        _code = Config.Get(c => c.OcrLanguageCode);
        _multiLangCode = Config.Get(c => c.LastOcrMultiLangCode);

        _enableOcr.Checked = Config.Get(c => c.EnableOcr);
        _ocrLang.SelectedIndexChanged += OcrLang_SelectedIndexChanged;
        _ocrMode.SelectedItem = configOcrMode;
        _ocrPreProcessing.Checked = Config.Get(c => c.OcrPreProcessing);
        _ocrAfterScanning.Checked = Config.Get(c => c.OcrAfterScanning);

        LoadLanguages();
        UpdateView();
    }

    protected override void BuildLayout()
    {
        FormStateController.Resizable = false;

        LayoutController.Content = L.Column(
            _enableOcr,
            L.Row(
                C.Label(UiStrings.OcrEngineLabel).AlignCenter().Padding(right: 40),
                _engineType.Scale()
            ).Aligned(),
            L.Row(
                C.Label(UiStrings.OcrLanguageLabel).AlignCenter().Padding(right: 40),
                _ocrLang.Scale()
            ).Aligned(),
            L.Row(
                C.Label(UiStrings.OcrModeLabel).AlignCenter().Padding(right: 40),
                _ocrMode.AsControl().Scale()
            ).Aligned().Visible(_tesseractVis),
            L.Row(
                C.Label(UiStrings.OcrGpuLabel).AlignCenter().Padding(right: 40),
                _gpuBackend.Scale()
            ).Aligned().Visible(_rapidOcrVis),
            _ocrPreProcessing,
            _ocrAfterScanning,
            C.Filler(),
            L.Row(
                _moreLanguages.AlignCenter().Padding(right: 30).Visible(_tesseractVis),
                C.Filler(),
                L.OkCancel(
                    C.OkButton(this, Save),
                    C.CancelButton(this))
            )
        );
    }

    private void LoadLanguages()
    {
        _suppressLangChangeEvent = true;

        if (_engineType.SelectedKey == "RapidOCR")
        {
            LoadRapidOcrLanguages();
        }
        else
        {
            LoadTesseractLanguages();
        }

        if (!string.IsNullOrEmpty(_code))
        {
            _ocrLang.SelectedKey = _code;
        }
        if (_ocrLang.SelectedIndex == -1)
        {
            _ocrLang.SelectedIndex = 0;
        }
        _suppressLangChangeEvent = false;
    }

    private void LoadTesseractLanguages()
    {
        var languages = _tesseractLanguageManager.InstalledLanguages
            .OrderBy(x => x.Name)
            .ToList();
        _ocrLang.Items.Clear();
        _ocrLang.Items.AddRange(languages.Select(lang => new ListItem
        {
            Key = lang.Code,
            Text = lang.Name
        }));
        if (languages.Count > 1)
        {
            if (_multiLangCode?.Contains("+") == true)
            {
                _ocrLang.Items.Add(new ListItem
                {
                    Key = _multiLangCode,
                    Text = string.Join($"{CultureInfo.CurrentCulture.TextInfo.ListSeparator} ",
                        _multiLangCode.Split('+')
                            .Select(code => languages.SingleOrDefault(lang => lang.Code == code)?.Name))
                });
            }
            _ocrLang.Items.Add(new ListItem
            {
                Key = "",
                Text = UiStrings.MultipleLanguages
            });
        }
    }

    private void LoadRapidOcrLanguages()
    {
        _ocrLang.Items.Clear();
        _ocrLang.Items.Add(new ListItem
        {
            Key = "latin",
            Text = "Latin (English, French, German, Spanish, ...)"
        });
    }

    private void UpdateView()
    {
        bool isEnabled = _enableOcr.IsChecked();
        bool isTesseract = _engineType.SelectedKey != "RapidOCR";

        _tesseractVis.IsVisible = isTesseract;
        _rapidOcrVis.IsVisible = !isTesseract;

        _enableOcr.Enabled = !Config.AppLocked.Has(c => c.EnableOcr);
        _engineType.Enabled = isEnabled;
        _ocrLang.Enabled = isEnabled && !Config.AppLocked.Has(c => c.OcrLanguageCode);
        _ocrMode.Enabled = isEnabled && !Config.AppLocked.Has(c => c.OcrMode);
        _gpuBackend.Enabled = isEnabled;
        _ocrPreProcessing.Enabled = isEnabled && !Config.AppLocked.Has(c => c.OcrPreProcessing);
        _ocrAfterScanning.Enabled = isEnabled && !Config.AppLocked.Has(c => c.OcrAfterScanning);
        _moreLanguages.Enabled = !Config.AppLocked.Has(c => c.OcrLanguageCode);
    }

    private void EnableOcr_CheckedChanged(object? sender, EventArgs e)
    {
        UpdateView();
    }

    private void EngineType_SelectedIndexChanged(object? sender, EventArgs e)
    {
        _code = null;
        LoadLanguages();
        UpdateView();
    }

    private void MoreLanguages_Click(object? sender, EventArgs e)
    {
        FormFactory.Create<OcrDownloadForm>().ShowModal();
        LoadLanguages();
    }

    private void OcrLang_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressLangChangeEvent) return;
        if (_engineType.SelectedKey != "RapidOCR" &&
            _ocrLang.SelectedIndex == _ocrLang.Items.Count - 1)
        {
            var multiLangForm = FormFactory.Create<OcrMultiLangForm>();
            multiLangForm.ShowModal();
            if (multiLangForm.Code != null)
            {
                _code = multiLangForm.Code;
            }
            if (multiLangForm.Code?.Contains("+") == true)
            {
                _multiLangCode = multiLangForm.Code;
            }
            LoadLanguages();
        }
        _code = _ocrLang.SelectedKey;
    }

    private void Save()
    {
        if (!Config.AppLocked.Has(c => c.EnableOcr))
        {
            var previousEngineType = Config.Get(c => c.OcrEngineType);
            var previousGpuBackend = Config.Get(c => c.RapidOcrGpuBackend);

            var transact = Config.User.BeginTransaction();
            transact.Set(c => c.EnableOcr, _enableOcr.IsChecked());
            transact.Set(c => c.OcrEngineType, _engineType.SelectedKey ?? "Tesseract");
            transact.Set(c => c.RapidOcrGpuBackend, _gpuBackend.SelectedKey ?? "auto");
            transact.Set(c => c.OcrLanguageCode, _ocrLang.SelectedKey);
            if (_multiLangCode?.Contains("+") == true)
            {
                Config.User.Set(c => c.LastOcrMultiLangCode, _multiLangCode);
            }
            transact.Set(c => c.OcrMode, _ocrMode.SelectedItem);
            transact.Set(c => c.OcrPreProcessing, _ocrPreProcessing.IsChecked());
            transact.Set(c => c.OcrAfterScanning, _ocrAfterScanning.IsChecked());
            transact.Commit();

            var newEngineType = _engineType.SelectedKey ?? "Tesseract";
            var newGpuBackend = _gpuBackend.SelectedKey ?? "auto";
            if (newEngineType != previousEngineType || newGpuBackend != previousGpuBackend)
            {
                var oldEngine = _scanningContext.OcrEngine;
                _scanningContext.OcrEngine = _ocrEngineFactory.Create();
                (oldEngine as IDisposable)?.Dispose();
            }
        }
    }
}