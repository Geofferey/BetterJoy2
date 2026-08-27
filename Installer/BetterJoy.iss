#define MyAppName "BetterJoy"
#define MyAppVersion "v7.2.1"
#define MyAppPublisher "BetterJoy Contributors"
#define MyAppURL "https://github.com/Geofferey/BetterJoy"
#define MyAppExeName "BetterJoyForCemu.exe"
#define MyBuildDir "..\BetterJoyForCemu\bin\x64\Release"
#define MyViGEmBusInstaller "ViGEmBus_1.22.0_x64_x86_arm64.exe"
#define MyHidHideInstaller "HidHide_1.5.230_x64.exe"
#define MyFakerInputInstaller "FakerInput_Setup_0.1.1_x64.msi"
#define MyUsbipInstaller "USBip-0.9.7.7-x64.exe"
#define MySteamMicInf "SteamStreamingMicrophone.inf"

[Setup]
; Same GUID as the project's ProjectGuid, so upgrades are detected correctly across releases.
AppId={{1BF709E9-C133-41DF-933A-C9FF3F664C7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=BetterJoy-Setup-{#MyAppVersion}
SetupIconFile=..\BetterJoyForCemu\Icons\betterjoyforcemu_icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "vigembus"; Description: "Install the ViGEmBus driver (required for XInput/DS4 output)"; GroupDescription: "Drivers:"; Flags: checkedonce
Name: "hidhide"; Description: "Install the HidHide driver (hides controllers from other programs, e.g. Steam)"; GroupDescription: "Drivers:"; Flags: unchecked
Name: "fakerinput"; Description: "Install FakerInput virtual mouse (works in elevated apps, UAC, and before login in service mode)"; GroupDescription: "Drivers:"; Flags: unchecked
Name: "dualsensemic"; Description: "Install the Bluetooth microphone backend (VIIPER + signed usbip-win2 driver, and the Steam Streaming Microphone driver as a fallback)"; GroupDescription: "Drivers:"; Flags: checkedonce

[Files]
; Everything from the Release build, except runtime-generated state that shouldn't ship pre-populated
; with whatever the machine that built it happened to have connected.
Source: "{#MyBuildDir}\*"; DestDir: "{app}"; Excludes: "settings,3rdPartyControllers,! Install the drivers in the Drivers folder"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\Drivers\{#MyViGEmBusInstaller}"; Parameters: "/quiet /norestart"; StatusMsg: "Installing ViGEmBus driver..."; Tasks: vigembus; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort: if the service was never installed these just fail quietly, which is fine -
; Inno doesn't treat a non-zero exit code here as an uninstall failure.
Filename: "{sys}\sc.exe"; Parameters: "stop BetterJoy"; Flags: runhidden waituntilterminated; RunOnceId: "StopBetterJoyService"
Filename: "{sys}\sc.exe"; Parameters: "delete BetterJoy"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteBetterJoyService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// HidHide is installed via Exec here (rather than a declarative [Run] line) so its exit code can
// be inspected: a WiX Burn bootstrapper returns 3010 (ERROR_SUCCESS_REBOOT_REQUIRED) when a
// reboot is needed, and NeedsRestart() below surfaces that as Inno's own native restart prompt
// instead of it being silently lost.
var
  HidHideExitCode: Integer;
  FakerInputExitCode: Integer;
  UsbipExitCode: Integer;
  SteamMicRebootRequired: Boolean;

// Real service-status polling via the SCM API - sc.exe stop only requests the stop and returns
// as soon as the SCM acknowledges the request, not once the service has actually finished
// shutting down (BetterJoyService.OnStop does real cleanup: unhiding HidHide devices,
// disconnecting ViGEm targets, stopping timers - not instant). Waiting merely for the sc.exe
// process to exit isn't the same as waiting for the file locks it's holding to be released.
const
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;
  SERVICE_RUNNING = 4;

type
  SERVICE_STATUS = record
    dwServiceType: LongWord;
    dwCurrentState: LongWord;
    dwControlsAccepted: LongWord;
    dwWin32ExitCode: LongWord;
    dwServiceSpecificExitCode: LongWord;
    dwCheckPoint: LongWord;
    dwWaitHint: LongWord;
  end;

function OpenSCManagerW(lpMachineName, lpDatabaseName: string; dwDesiredAccess: LongWord): LongWord;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenServiceW(hSCManager: LongWord; lpServiceName: string; dwDesiredAccess: LongWord): LongWord;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(hService: LongWord; var lpServiceStatus: SERVICE_STATUS): BOOL;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(hSCObject: LongWord): BOOL;
  external 'CloseServiceHandle@advapi32.dll stdcall';

// Returns the service's current SERVICE_* state, or 0 if it isn't installed / can't be queried
// (never installed, access denied, etc.) - 0 isn't a real SERVICE_* value, so callers can treat
// it as "unknown/absent" without confusing it for a real state.
function GetServiceState(ServiceName: string): LongWord;
var
  SCManager, Service: LongWord;
  Status: SERVICE_STATUS;
begin
  Result := 0;
  SCManager := OpenSCManagerW('', '', SC_MANAGER_CONNECT);
  if SCManager = 0 then
    exit;
  try
    Service := OpenServiceW(SCManager, ServiceName, SERVICE_QUERY_STATUS);
    if Service = 0 then
      exit;
    try
      if QueryServiceStatus(Service, Status) then
        Result := Status.dwCurrentState;
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(SCManager);
  end;
end;

// FakerInput is an MSI rather than a bootstrapper. It stays an explicit installer task because
// virtual input is optional and installing a system driver should always be a conscious choice.
procedure InstallFakerInput;
var
  ResultCode: Integer;
  Params: String;
begin
  if WizardIsTaskSelected('fakerinput') then begin
    Params := '/i "' + ExpandConstant('{app}\Drivers\{#MyFakerInputInstaller}') + '" /qn /norestart';
    if Exec(ExpandConstant('{sys}\msiexec.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      FakerInputExitCode := ResultCode
    else
      FakerInputExitCode := -1;
  end;
end;

procedure InstallHidHide;
var
  ResultCode: Integer;
begin
  if WizardIsTaskSelected('hidhide') then begin
    if Exec(ExpandConstant('{app}\Drivers\{#MyHidHideInstaller}'), '/exenoui /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      HidHideExitCode := ResultCode
    else
      HidHideExitCode := -1;
  end;
end;

// VIIPER itself is a bundled user-mode sidecar started on demand by BetterJoy. Only usbip-win2
// needs installation: it supplies the signed virtual USB host controller that exposes VIIPER's
// DualSense audio-only device as an ordinary Windows recording endpoint.
procedure InstallDualSenseMicrophoneBackend;
var
  ResultCode: Integer;
begin
  if WizardIsTaskSelected('dualsensemic') then begin
    if Exec(ExpandConstant('{app}\Drivers\{#MyUsbipInstaller}'),
        '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010', '', SW_HIDE,
        ewWaitUntilTerminated, ResultCode) then
      UsbipExitCode := ResultCode
    else
      UsbipExitCode := -1;
  end;
end;

// Steam Streaming Microphone (SteamMicrophoneEndpoint.cs's fallback) is a root-enumerated
// (non-PnP) MEDIA-class device: unlike an ordinary PnP driver, staging the INF into the driver
// store isn't enough - the devnode itself has to be created before a driver can bind to it.
// pnputil's /add-driver ... /install only updates a device that already exists (confirmed via
// Microsoft's own guidance recommending it specifically to AVOID creating a root-enumerated
// device); devcon's "install" command is the documented way to create one, but devcon.exe isn't
// redistributable outside the WDK. This calls the same underlying SetupAPI sequence devcon uses
// internally instead - verified end-to-end against the real bundled INF on real hardware before
// writing this (SetupDiCreateDeviceInfoW's DeviceName parameter becomes the generated instance
// ID's own segment, e.g. passing "SteamStreamingMicrophone" here yields
// ROOT\SteamStreamingMicrophone\0000, not just a class-name lookup).
// The bundled INF/CAT pair is Valve's own, unmodified - a CAT file's signature covers the hash of
// every file it lists, including the INF itself, so editing the INF's strings (tried once, to give
// this its own distinct hardware ID) breaks that hash and Windows refuses it
// (ERROR_FILE_HASH_NOT_IN_CATALOG), right back to the "needs test-signing mode" problem this
// driver was chosen specifically to avoid. So this intentionally targets the exact same hardware
// ID Steam's own installer would use - if Steam already created it (or does later), this step is a
// harmless no-op (see SteamMicDeviceAlreadyExists below). SteamMicrophoneEndpoint.cs re-applies a
// distinguishing friendly name to the endpoint on every open instead, since that's a plain
// registry property unrelated to driver signing and (unlike editing the INF) survives Steam
// recreating the device later.
const
  DIGCF_PRESENT = $00000002;
  DICD_GENERATE_ID = $00000001;
  SPDRP_HARDWAREID = $00000001;
  DIF_REGISTERDEVICE = $00000019;
  DIF_REMOVE = $00000005;
  INSTALLFLAG_FORCE = $00000001;
  INSTALLFLAG_NONINTERACTIVE = $00000004;
  SteamMicHardwareId = 'ROOT\SteamStreamingMicrophone';
  SteamMicDeviceName = 'SteamStreamingMicrophone';

type
  TDeviceGuid = record
    D1: LongWord;
    D2: Word;
    D3: Word;
    D4: array[0..7] of Byte;
  end;

  SP_DEVINFO_DATA = record
    cbSize: LongWord;
    ClassGuid: TDeviceGuid;
    DevInst: LongWord;
    Reserved: LongInt;
  end;

function SetupDiGetClassDevsW(ClassGuid: TDeviceGuid; Enumerator: LongInt; hwndParent: LongWord; Flags: LongWord): LongWord;
  external 'SetupDiGetClassDevsW@setupapi.dll stdcall';
function SetupDiEnumDeviceInfo(DeviceInfoSet: LongWord; MemberIndex: LongWord; var DeviceInfoData: SP_DEVINFO_DATA): BOOL;
  external 'SetupDiEnumDeviceInfo@setupapi.dll stdcall';
function SetupDiGetDeviceRegistryPropertyW(DeviceInfoSet: LongWord; var DeviceInfoData: SP_DEVINFO_DATA; Prop: LongWord; var PropRegDataType: LongWord; PropertyBuffer: string; PropertyBufferSize: LongWord; var RequiredSize: LongWord): BOOL;
  external 'SetupDiGetDeviceRegistryPropertyW@setupapi.dll stdcall';
function SetupDiCreateDeviceInfoList(ClassGuid: TDeviceGuid; hwndParent: LongWord): LongWord;
  external 'SetupDiCreateDeviceInfoList@setupapi.dll stdcall';
function SetupDiCreateDeviceInfoW(DeviceInfoSet: LongWord; DeviceName: string; ClassGuid: TDeviceGuid; DeviceDescription: string; hwndParent: LongWord; CreationFlags: LongWord; var DeviceInfoData: SP_DEVINFO_DATA): BOOL;
  external 'SetupDiCreateDeviceInfoW@setupapi.dll stdcall';
function SetupDiSetDeviceRegistryPropertyW(DeviceInfoSet: LongWord; var DeviceInfoData: SP_DEVINFO_DATA; Prop: LongWord; PropertyBuffer: string; PropertyBufferSize: LongWord): BOOL;
  external 'SetupDiSetDeviceRegistryPropertyW@setupapi.dll stdcall';
function SetupDiCallClassInstaller(InstallFunction: LongWord; DeviceInfoSet: LongWord; var DeviceInfoData: SP_DEVINFO_DATA): BOOL;
  external 'SetupDiCallClassInstaller@setupapi.dll stdcall';
function SetupDiDestroyDeviceInfoList(DeviceInfoSet: LongWord): BOOL;
  external 'SetupDiDestroyDeviceInfoList@setupapi.dll stdcall';
function UpdateDriverForPlugAndPlayDevicesW(hwndParent: LongWord; HardwareId: string; FullInfPath: string; InstallFlags: LongWord; var bRebootRequired: BOOL): BOOL;
  external 'UpdateDriverForPlugAndPlayDevicesW@newdev.dll stdcall';

function SteamMicClassGuid: TDeviceGuid;
begin
  // {4d36e96c-e325-11ce-bfc1-08002be10318} - MEDIA, matches the INF's own [Version] ClassGuid.
  Result.D1 := $4d36e96c;
  Result.D2 := $e325;
  Result.D3 := $11ce;
  Result.D4[0] := $bf; Result.D4[1] := $c1; Result.D4[2] := $08; Result.D4[3] := $00;
  Result.D4[4] := $2b; Result.D4[5] := $e1; Result.D4[6] := $03; Result.D4[7] := $18;
end;

// A device's HARDWAREID property is a REG_MULTI_SZ (list of null-separated strings, double-null
// terminated) - only the first entry is ever set here, so comparing up to the first embedded null
// is enough to know whether it's our hardware ID.
function DeviceHardwareIdMatches(DeviceInfoSet: LongWord; var DeviceInfoData: SP_DEVINFO_DATA; TargetHardwareId: string): Boolean;
var
  PropType, RequiredSize: LongWord;
  Buffer: string;
  NullPos: Integer;
begin
  Result := False;
  SetLength(Buffer, 512);
  if not SetupDiGetDeviceRegistryPropertyW(DeviceInfoSet, DeviceInfoData, SPDRP_HARDWAREID,
      PropType, Buffer, Length(Buffer) * 2, RequiredSize) then
    exit;
  NullPos := Pos(#0, Buffer);
  if NullPos > 0 then
    Buffer := Copy(Buffer, 1, NullPos - 1);
  Result := CompareText(Buffer, TargetHardwareId) = 0;
end;

// Repeat installs/upgrades must not create a second devnode - checked this the hard way while
// verifying this code: re-running the create sequence without a guard produced a second full set
// of render+capture endpoints alongside the original one instead of reusing it. Also true,
// harmlessly, if Steam itself already created the device via its own first-run trigger before
// BetterJoy was ever installed - same hardware ID either way (see comment above).
function SteamMicDeviceAlreadyExists: Boolean;
var
  DevInfoSet: LongWord;
  DevInfoData: SP_DEVINFO_DATA;
  Index: LongWord;
begin
  Result := False;
  DevInfoSet := SetupDiGetClassDevsW(SteamMicClassGuid, 0, 0, DIGCF_PRESENT);
  if DevInfoSet = 0 then
    exit;
  try
    Index := 0;
    DevInfoData.cbSize := SizeOf(DevInfoData);
    while SetupDiEnumDeviceInfo(DevInfoSet, Index, DevInfoData) do begin
      if DeviceHardwareIdMatches(DevInfoSet, DevInfoData, SteamMicHardwareId) then begin
        Result := True;
        exit;
      end;
      Index := Index + 1;
      DevInfoData.cbSize := SizeOf(DevInfoData);
    end;
  finally
    SetupDiDestroyDeviceInfoList(DevInfoSet);
  end;
end;

// Mirrors devcon's own "install" command: create a root-enumerated devnode with the target
// hardware ID, then bind the bundled INF's driver to it. Verified against the real bundled INF on
// real hardware before this was written (see comment above). On a failed driver bind, rolls the
// devnode back out rather than leaving an orphaned, driverless "Steam Streaming Microphone" entry
// in Device Manager.
procedure InstallSteamStreamingMicrophone;
var
  DevInfoSet: LongWord;
  DevInfoData: SP_DEVINFO_DATA;
  ClassGuid: TDeviceGuid;
  RebootRequired: BOOL;
  InfPath: string;
  Created, Bound: Boolean;
begin
  if not WizardIsTaskSelected('dualsensemic') then
    exit;
  if SteamMicDeviceAlreadyExists then
    exit;

  ClassGuid := SteamMicClassGuid;
  Created := False;
  Bound := False;
  DevInfoSet := SetupDiCreateDeviceInfoList(ClassGuid, 0);
  if DevInfoSet = 0 then
    exit;
  try
    DevInfoData.cbSize := SizeOf(DevInfoData);
    if not SetupDiCreateDeviceInfoW(DevInfoSet, SteamMicDeviceName, ClassGuid, '', 0,
        DICD_GENERATE_ID, DevInfoData) then
      exit;

    if not SetupDiSetDeviceRegistryPropertyW(DevInfoSet, DevInfoData, SPDRP_HARDWAREID,
        SteamMicHardwareId + #0 + #0, (Length(SteamMicHardwareId) + 2) * 2) then
      exit;

    if not SetupDiCallClassInstaller(DIF_REGISTERDEVICE, DevInfoSet, DevInfoData) then
      exit;
    Created := True;

    InfPath := ExpandConstant('{app}\Drivers\{#MySteamMicInf}');
    RebootRequired := False;
    Bound := UpdateDriverForPlugAndPlayDevicesW(0, SteamMicHardwareId, InfPath,
      INSTALLFLAG_FORCE or INSTALLFLAG_NONINTERACTIVE, RebootRequired);
    if Bound then
      SteamMicRebootRequired := RebootRequired
    else if Created then
      SetupDiCallClassInstaller(DIF_REMOVE, DevInfoSet, DevInfoData);
  finally
    SetupDiDestroyDeviceInfoList(DevInfoSet);
  end;
end;

// sc.exe's binPath value has to be one single argument containing the (space-containing,
// quoted) exe path followed by " -service" - the outer quotes let the command-line parser
// treat the whole thing as one token for sc.exe, the escaped inner quotes are what sc.exe
// itself then records as the actual service binary path.
// MainForm has no local-ownership fallback anymore (see clever-wiggling-rocket.md) - the GUI is
// non-functional without the service running, so unlike the optional driver tasks above this
// always runs, no checkbox to skip it. `sc create` on an already-existing service (a plain
// upgrade) just fails harmlessly; the calls after it still bring the service to the desired
// state regardless. The failure action registers automatic recovery (see BetterJoyService.OnStop
// - a crash otherwise leaves the GUI permanently unable to reconnect until someone notices and
// restarts it by hand): 3 restart attempts a second apart, and resetperiod resets the failure
// count after a full day with no further crashes rather than accumulating forever.
procedure InstallService;
var
  ResultCode: Integer;
  Params: String;
begin
  Params := 'create BetterJoy binPath= "\"' + ExpandConstant('{app}\{#MyAppExeName}') + '\" -service" start= auto DisplayName= "BetterJoy"';
  Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'description BetterJoy "Nintendo Switch controller service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure BetterJoy reset= 86400 actions= restart/1000/restart/1000/restart/1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'start BetterJoy', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Before files are copied: an existing installed service still running keeps
// BetterJoyForCemu.exe/its DLLs locked, which fails the file-copy step outright rather than a
// clean upgrade. InstallService always restarts the service afterward regardless of how this
// left it, so there's nothing to remember here beyond "was it running" for the exit-early check.
procedure StopExistingService;
var
  ResultCode: Integer;
  Attempts: Integer;
begin
  if GetServiceState('BetterJoy') <> SERVICE_RUNNING then
    exit;

  Exec(ExpandConstant('{sys}\sc.exe'), 'stop BetterJoy', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Poll for the service to actually reach SERVICE_STOPPED, not just for the sc.exe command to
  // return - up to 10 seconds, then give up and let the file copy fail loudly if it must rather
  // than hang the installer indefinitely on a service that's stuck shutting down.
  Attempts := 0;
  while (GetServiceState('BetterJoy') <> SERVICE_STOPPED) and (Attempts < 50) do begin
    Sleep(200);
    Attempts := Attempts + 1;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    StopExistingService;
  end;
  if CurStep = ssPostInstall then begin
    InstallFakerInput;
    InstallHidHide;
    InstallDualSenseMicrophoneBackend;
    InstallSteamStreamingMicrophone;
    InstallService;
  end;
end;

function NeedsRestart(): Boolean;
begin
  Result := (HidHideExitCode = 3010) or
    (FakerInputExitCode = 3010) or (FakerInputExitCode = 1641) or
    (UsbipExitCode = 3010) or (UsbipExitCode = 1641) or
    SteamMicRebootRequired;
end;
