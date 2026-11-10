; ============================================================
;  Inno Setup: установщик «Зум-зум» для AutoCAD / Civil 3D
;  Сборка:    .\build-installer.ps1 [-Version "1.1.1"]
;  Результат: dist\Setup_ZoomZoom_v<версия>.exe
;
;  Ставит ZoomZoom.bundle в %APPDATA%\Autodesk\ApplicationPlugins
;  для текущего пользователя, без прав администратора. AutoCAD сам
;  выбирает из бандла сборку под свою версию (PackageContents.xml).
;
;  Язык выбирает пользователь в первом окне. Заранее выбран язык Windows
;  (английская — английский, любая другая — русский: первый в [Languages]
;  берётся, когда подходящего нет), при переустановке — язык прошлой
;  установки. Выбранный язык — это язык и мастера, и самого плагина: он
;  пишется в ZoomZoom.ini в корне бандла (см. [INI]), плагин читает его
;  при запуске AutoCAD (RescaleApp.InstallerLanguage).
; ============================================================

#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif

#define AppPublisher "Zaycsev"
#define AppURL       "https://github.com/zaycsev/ZoomZoom"
#define BundleSrc    "dist\bundle"
#define InstDir      "{userappdata}\Autodesk\ApplicationPlugins\ZoomZoom.bundle"

; ============================================================
[Setup]
AppId={{71908399-4070-4333-8FF7-0ED7678E1FC2}
AppName={cm:AppName}
AppVersion={#AppVersion}
AppVerName={cm:AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases
AppCopyright=Copyright (C) 2026 zaycsev
VersionInfoVersion={#AppVersion}
VersionInfoDescription=ZoomZoom (Зум-зум) setup for AutoCAD and Civil 3D

DefaultDirName={#InstDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateUninstallRegKey=yes
UninstallDisplayName={cm:UninstallName}
UninstallDisplayIcon={app}\setup.ico

PrivilegesRequired=lowest

OutputDir=dist
OutputBaseFilename=Setup_ZoomZoom_v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

SetupIconFile=installer\setup.ico
; Несколько размеров — под масштаб экрана 100/150/200%, Inno берёт ближайший.
WizardImageFile=installer\wizard_side_100.png,installer\wizard_side_150.png,installer\wizard_side_200.png
WizardSmallImageFile=installer\wizard_small_100.png,installer\wizard_small_150.png,installer\wizard_small_200.png

WizardStyle=modern
DisableWelcomePage=no
; Окно выбора языка — всегда, даже если язык Windows совпал с одним из списка.
ShowLanguageDialog=yes

; ============================================================
[Languages]
; Первый — язык по умолчанию для Windows на любом другом языке.
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

; ============================================================
[Messages]
russian.SelectLanguageLabel=Выберите язык установки. На этом же языке будут кнопки и окна Зум-зума в AutoCAD.
english.SelectLanguageLabel=Select the setup language. ZoomZoom buttons and windows in AutoCAD will use the same language.
russian.FinishedLabel=Установка [name/ver] завершена.%n%nПанель «Зум-зум» появится на вкладке «Надстройки» при следующем запуске AutoCAD или Civil 3D.%n%nКоманды: ZZ (настройки), ZZALL, ZZSEL, ZZREF, DWGSPLIT, DWGMERGE, DWGMOVEVP, DWGVIEWS.
english.FinishedLabel=[name/ver] has been installed.%n%nThe ZoomZoom panel will appear on the Add-ins tab the next time you start AutoCAD or Civil 3D.%n%nCommands: ZZ (settings), ZZALL, ZZSEL, ZZREF, DWGSPLIT, DWGMERGE, DWGMOVEVP, DWGVIEWS.

; ============================================================
[CustomMessages]
; Приветствие — по строке на абзац и пункт списка; собирается в BuildWelcome.
russian.WelcomeIntro=Установка Зум-зум в панель на ленте AutoCAD и Civil 3D:
russian.WelcomeItem1=Весь чертёж, Рамка, Отрезок, Настройки — масштабирование с сохранением исходной композиции
russian.WelcomeItem2=Подсветить экраны на чертеже
russian.WelcomeItem3=Передвинуть объекты чертежа вместе с видовым экраном
russian.WelcomeItem4=Разделить DWG по листам в отдельные файлы и собрать в один DWG
russian.WelcomeVersions=Поддерживаемые версии:
russian.WelcomeVersion1=AutoCAD / Civil 3D 2015–2018 (.NET Framework 4.5)
russian.WelcomeVersion2=AutoCAD / Civil 3D 2019–2024 (.NET Framework 4.7)
russian.WelcomeVersion3=AutoCAD / Civil 3D 2025 и новее (.NET 8)
russian.WelcomeClose=Закройте AutoCAD перед продолжением.
english.WelcomeIntro=Installing ZoomZoom as a panel on the AutoCAD and Civil 3D ribbon:
english.WelcomeItem1=Whole Drawing, Window, Reference, Settings — rescaling that keeps the original composition
english.WelcomeItem2=Highlight viewports on the drawing
english.WelcomeItem3=Move drawing objects together with their viewport
english.WelcomeItem4=Split a DWG into separate files by layout and merge them into one DWG
english.WelcomeVersions=Supported versions:
english.WelcomeVersion1=AutoCAD / Civil 3D 2015–2018 (.NET Framework 4.5)
english.WelcomeVersion2=AutoCAD / Civil 3D 2019–2024 (.NET Framework 4.7)
english.WelcomeVersion3=AutoCAD / Civil 3D 2025 and later (.NET 8)
english.WelcomeClose=Please close AutoCAD before continuing.
russian.AppName=Зум-зум
english.AppName=ZoomZoom
russian.UninstallName=Зум-зум для AutoCAD и Civil 3D
english.UninstallName=ZoomZoom for AutoCAD and Civil 3D
russian.NoAcad1=AutoCAD или Civil 3D на этом компьютере не найден.
russian.NoAcad2=Зум-зум работает в AutoCAD / Civil 3D 2015 и новее.
russian.NoAcad3=Всё равно установить?
english.NoAcad1=AutoCAD or Civil 3D was not found on this computer.
english.NoAcad2=ZoomZoom works in AutoCAD / Civil 3D 2015 and later.
english.NoAcad3=Install anyway?
russian.Running1=AutoCAD или Civil 3D сейчас запущен.
russian.Running2=Закройте его (сохранив работу) и нажмите «Повторить».
english.Running1=AutoCAD or Civil 3D is running.
english.Running2=Close it (save your work first) and click Retry.
russian.UninstRunning1=AutoCAD или Civil 3D сейчас запущен и держит файлы Зум-зума.
russian.UninstRunning2=Закройте его и нажмите «Да», чтобы продолжить удаление.
english.UninstRunning1=AutoCAD or Civil 3D is running and holds the ZoomZoom files.
english.UninstRunning2=Close it and click Yes to continue uninstalling.

; ============================================================
[InstallDelete]
; Contents пересоздаётся целиком: DLL от прежней версии не должна остаться.
Type: filesandordirs; Name: "{app}\Contents"
; Прежние отдельные плагины: их команды определились бы второй раз.
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\AcadRescale.bundle"
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\DwGangsta.bundle"

; ============================================================
[Files]
Source: "{#BundleSrc}\PackageContents.xml"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BundleSrc}\Contents\net45\ZoomZoom.dll"; DestDir: "{app}\Contents\net45"; Flags: ignoreversion
Source: "{#BundleSrc}\Contents\net47\ZoomZoom.dll"; DestDir: "{app}\Contents\net47"; Flags: ignoreversion
Source: "{#BundleSrc}\Contents\net8.0-windows\ZoomZoom.dll"; DestDir: "{app}\Contents\net8.0-windows"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "installer\setup.ico"; DestDir: "{app}"; Flags: ignoreversion
; Полоса мастера с английской подписью — только для самого мастера (см. InitializeWizard).
Source: "installer\wizard_side_en_*.png"; Flags: dontcopy

; ============================================================
[INI]
; Язык, выбранный в мастере, — для самого плагина: english или russian.
Filename: "{app}\ZoomZoom.ini"; Section: "ZoomZoom"; Key: "Language"; String: "{language}"

; ============================================================
[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; ============================================================
[Code]

// AutoCAD и его вертикали регистрируются в HKLM\SOFTWARE\Autodesk\AutoCAD\R<серия>.
function AutoCadInstalled(): Boolean;
var
  Series: TArrayOfString;
  i: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\Autodesk\AutoCAD', Series) then
    for i := 0 to GetArrayLength(Series) - 1 do
      if Copy(Series[i], 1, 1) = 'R' then
      begin
        Result := True;
        Exit;
      end;
end;

// Абзац приветствия под предыдущим (Y — его верх, после вызова — низ). У пункта
// списка точка — отдельной надписью, а текст — своим полем с отступом: строка,
// перенесённая по ширине, начинается под текстом пункта, а не под точкой.
// Штатное поле приветствия так не умеет — у него один текст с переносом по краю.
procedure AddWelcomeText(const Text: String; Bullet: Boolean; var Y: Integer);
var
  Mark, Body: TNewStaticText;
  Indent: Integer;
begin
  Indent := 0;
  if Bullet then
  begin
    Indent := ScaleX(12);
    Mark := TNewStaticText.Create(WizardForm);
    Mark.Left := WizardForm.WelcomeLabel2.Left + ScaleX(1);
    Mark.Top := Y;
    Mark.Caption := '•';
    Mark.Parent := WizardForm.WelcomePage;
  end;
  Body := TNewStaticText.Create(WizardForm);
  Body.AutoSize := False;
  Body.WordWrap := True;
  Body.Left := WizardForm.WelcomeLabel2.Left + Indent;
  Body.Top := Y;
  Body.Width := WizardForm.WelcomeLabel2.Width - Indent;
  Body.Caption := Text;
  Body.Parent := WizardForm.WelcomePage;
  Body.AdjustHeight;
  Y := Y + Body.Height;
end;

// Приветствие вместо штатного WelcomeLabel2: тексты — из [CustomMessages], пустая
// строка между блоками — высотой в строку того же шрифта.
procedure BuildWelcome();
var
  Probe: TNewStaticText;
  Y, Gap: Integer;
begin
  Probe := TNewStaticText.Create(WizardForm);
  Probe.Caption := 'Ag';
  Probe.Parent := WizardForm.WelcomePage;
  Gap := Probe.Height;
  Probe.Free;

  WizardForm.WelcomeLabel2.Visible := False;
  Y := WizardForm.WelcomeLabel2.Top;
  AddWelcomeText(CustomMessage('WelcomeIntro'), False, Y);
  Y := Y + Gap;
  AddWelcomeText(CustomMessage('WelcomeItem1'), True, Y);
  AddWelcomeText(CustomMessage('WelcomeItem2'), True, Y);
  AddWelcomeText(CustomMessage('WelcomeItem3'), True, Y);
  AddWelcomeText(CustomMessage('WelcomeItem4'), True, Y);
  Y := Y + Gap;
  AddWelcomeText(CustomMessage('WelcomeVersions'), False, Y);
  AddWelcomeText(CustomMessage('WelcomeVersion1'), True, Y);
  AddWelcomeText(CustomMessage('WelcomeVersion2'), True, Y);
  AddWelcomeText(CustomMessage('WelcomeVersion3'), True, Y);
  Y := Y + Gap;
  AddWelcomeText(CustomMessage('WelcomeClose'), False, Y);
  Y := Y + Gap;
  AddWelcomeText(SetupMessage(msgClickNext), False, Y);
end;

// Картинка слева на первой и последней странице: в английском мастере — с
// английской подписью. Размер под масштаб экрана Inno выбирает сам только для
// картинок из [Setup]; здесь — по ширине, которую картинка займёт на экране
// (берётся та, что не меньше: уменьшение чётче растяжения).
procedure InitializeWizard();
var
  Name: String;
begin
  BuildWelcome();

  if ActiveLanguage() <> 'english' then Exit;
  if WizardForm.WizardBitmapImage.Width > 246 then Name := 'wizard_side_en_200.png'
  else if WizardForm.WizardBitmapImage.Width > 164 then Name := 'wizard_side_en_150.png'
  else Name := 'wizard_side_en_100.png';
  ExtractTemporaryFile(Name);
  WizardForm.WizardBitmapImage.PngImage.LoadFromFile(ExpandConstant('{tmp}\') + Name);
  WizardForm.WizardBitmapImage2.PngImage.LoadFromFile(ExpandConstant('{tmp}\') + Name);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not AutoCadInstalled() then
    Result := MsgBox(CustomMessage('NoAcad1') + #13#10 +
                     CustomMessage('NoAcad2') + #13#10#13#10 +
                     CustomMessage('NoAcad3'), mbConfirmation, MB_YESNO) = IDYES;
end;

// Запущенный AutoCAD держит ZoomZoom.dll — заменить её нельзя.
function AutoCadRunning(): Boolean;
var
  Wbem, Procs: Variant;
begin
  Result := False;
  try
    Wbem := CreateOleObject('WbemScripting.SWbemLocator');
    Procs := Wbem.ConnectServer('', 'root\cimv2').ExecQuery(
      'SELECT ProcessId FROM Win32_Process WHERE Name="acad.exe"');
    Result := Procs.Count > 0;
  except
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if AutoCadRunning() then
    Result := CustomMessage('Running1') + #13#10 + CustomMessage('Running2');
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  while Result and AutoCadRunning() do
    Result := MsgBox(CustomMessage('UninstRunning1') + #13#10 + CustomMessage('UninstRunning2'),
                     mbError, MB_YESNO) = IDYES;
end;
