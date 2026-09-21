param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\BetterJoyForCemu\bin\x64\Release\BetterJoy2.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Configuration

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

$checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    $script:checks++
    Assert-True $Condition $Message
}

$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
Push-Location (Split-Path -Parent $resolvedAssembly)
try {
    $assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
    $controllerType = $assembly.GetType('BetterJoyForCemu.Controller', $true)
    $mappingsType = $assembly.GetType('BetterJoyForCemu.ControllerMappings', $true)
    $reassignType = $assembly.GetType('BetterJoyForCemu.Reassign', $true)
    $inputStateType = $assembly.GetType('BetterJoyForCemu.InputState', $true)
    $kindType = $assembly.GetType('BetterJoyForCemu.ControllerKind', $true)
    $staticNonPublic = [Reflection.BindingFlags]'Static,NonPublic'

    $deadzone = $controllerType.GetMethod('ApplyStickDeadzone', $staticNonPublic)
    $curve = $controllerType.GetMethod('ApplyStickResponseCurve', $staticNonPublic)
    $mouseDelta = $controllerType.GetMethod('ComputeStickMouseDelta', $staticNonPublic)
    $keyMask = $controllerType.GetMethod('ComputeStickKeyMask', $staticNonPublic)

    function Invoke-Deadzone([float]$X, [float]$Y, [int]$Percent) {
        $arguments = New-Object object[] 5
        $arguments[0] = [float]$X
        $arguments[1] = [float]$Y
        $arguments[2] = [int]$Percent
        $deadzone.Invoke($null, $arguments) | Out-Null
        return @([float]$arguments[3], [float]$arguments[4])
    }

    function Invoke-MouseDelta([float]$X, [float]$Y, [int]$Deadzone, [string]$Curve,
                               [int]$SensitivityX, [int]$SensitivityY, [float]$Seconds) {
        $arguments = New-Object object[] 9
        $arguments[0] = [float]$X
        $arguments[1] = [float]$Y
        $arguments[2] = [int]$Deadzone
        $arguments[3] = [string]$Curve
        $arguments[4] = [int]$SensitivityX
        $arguments[5] = [int]$SensitivityY
        $arguments[6] = [float]$Seconds
        $mouseDelta.Invoke($null, $arguments) | Out-Null
        return @([float]$arguments[7], [float]$arguments[8])
    }

    # User contract: a push inside the deadzone produces nothing, and everything past it is
    # rescaled across the full range rather than clipped - so movement starts as a crawl instead
    # of jumping to the speed the raw deflection would have given.
    $inside = Invoke-Deadzone 0.10 0.0 15
    Assert-Check ($inside[0] -eq 0.0 -and $inside[1] -eq 0.0) `
        'A push inside the deadzone still produced stick output.'
    $full = Invoke-Deadzone 1.0 0.0 15
    Assert-Check ([Math]::Abs($full[0] - 1.0) -lt 0.0001) `
        'Full deflection did not still reach full output after the deadzone rescale.'
    $halfway = Invoke-Deadzone 0.575 0.0 15
    Assert-Check ([Math]::Abs($halfway[0] - 0.5) -lt 0.01) `
        "Output past the deadzone was clipped rather than rescaled (got $($halfway[0]), expected ~0.5)."
    # Direction has to survive the rescale, or a 45-degree push would stop travelling at 45.
    $diagonal = Invoke-Deadzone 0.6 0.6 15
    Assert-Check ([Math]::Abs($diagonal[0] - $diagonal[1]) -lt 0.0001) `
        'The deadzone rescale bent a diagonal push off its own axis.'

    # User contract: every curve leaves the endpoints alone and rises monotonically, and the
    # shaped ones sit below linear in between - that is what keeps small pushes precise.
    foreach ($curveName in 'linear', 'quadratic', 'cubic') {
        Assert-Check ([float]$curve.Invoke($null, @([float]0.0, $curveName)) -eq 0.0) `
            "Curve $curveName did not leave a centered stick at rest."
        Assert-Check ([Math]::Abs([float]$curve.Invoke($null, @([float]1.0, $curveName)) - 1.0) -lt 0.0001) `
            "Curve $curveName did not reach full speed at full deflection."
        $previous = -1.0
        foreach ($sample in 0.0, 0.2, 0.4, 0.6, 0.8, 1.0) {
            $value = [float]$curve.Invoke($null, @([float]$sample, $curveName))
            Assert-Check ($value -ge $previous) "Curve $curveName was not monotonic at $sample."
            $previous = $value
        }
    }
    $linearMid = [float]$curve.Invoke($null, @([float]0.5, 'linear'))
    $quadraticMid = [float]$curve.Invoke($null, @([float]0.5, 'quadratic'))
    $cubicMid = [float]$curve.Invoke($null, @([float]0.5, 'cubic'))
    Assert-Check ($cubicMid -lt $quadraticMid -and $quadraticMid -lt $linearMid) `
        'The shaped curves were not slower than linear at half deflection.'
    Assert-Check ([float]$curve.Invoke($null, @([float]0.5, 'nonsense')) -eq $linearMid) `
        'An unrecognized curve value did not fall back to linear.'

    # User contract: a diagonal push holds the two neighbouring keys (up AND right), which is
    # what a game expecting WASD wants - so the threshold is applied per axis, not radially.
    $up = 1; $down = 2; $left = 4; $right = 8
    Assert-Check ([int]$keyMask.Invoke($null, @([float]0.8, [float]0.8, 55)) -eq ($up -bor $right)) `
        'A diagonal push did not hold both neighbouring direction keys.'
    Assert-Check ([int]$keyMask.Invoke($null, @([float]-0.8, [float]-0.8, 55)) -eq ($down -bor $left)) `
        'A down-left push did not hold both neighbouring direction keys.'
    Assert-Check ([int]$keyMask.Invoke($null, @([float]0.0, [float]0.9, 55)) -eq $up) `
        'A straight-up push did not hold exactly the up key.'
    Assert-Check ([int]$keyMask.Invoke($null, @([float]0.5, [float]0.5, 55)) -eq 0) `
        'A push below the threshold still held a direction key.'
    Assert-Check ([int]$keyMask.Invoke($null, @([float]0.0, [float]0.0, 55)) -eq 0) `
        'A centered stick held a direction key.'
    # One axis cannot be pushed both ways, so these pairs must never co-occur at any input.
    foreach ($sampleX in -1.0, -0.6, 0.0, 0.6, 1.0) {
        foreach ($sampleY in -1.0, -0.6, 0.0, 0.6, 1.0) {
            $mask = [int]$keyMask.Invoke($null, @([float]$sampleX, [float]$sampleY, 55))
            Assert-Check (($mask -band ($up -bor $down)) -ne ($up -bor $down)) `
                "Up and Down were held together at ($sampleX, $sampleY)."
            Assert-Check (($mask -band ($left -bor $right)) -ne ($left -bor $right)) `
                "Left and Right were held together at ($sampleX, $sampleY)."
        }
    }

    # User contract: sensitivity is per axis and scales the pointer proportionally, and pushing
    # the stick up moves the pointer up - the desktop's Y grows downward, so dy must be negative.
    $base = Invoke-MouseDelta 1.0 0.0 0 'linear' 100 100 0.01
    $doubled = Invoke-MouseDelta 1.0 0.0 0 'linear' 200 100 0.01
    Assert-Check ([Math]::Abs($doubled[0] - 2.0 * $base[0]) -lt 0.001) `
        'Doubling sensitivity X did not double the horizontal pointer delta.'
    Assert-Check ([Math]::Abs($base[0] - 10.0) -lt 0.001) `
        "100% sensitivity at full deflection was not 1000 px/s (got $($base[0]) px in 10 ms)."
    $upward = Invoke-MouseDelta 0.0 1.0 0 'linear' 100 100 0.01
    Assert-Check ($upward[1] -lt 0.0) `
        'Pushing the stick up moved the pointer down.'
    $centered = Invoke-MouseDelta 0.0 0.0 15 'quadratic' 100 100 0.01
    Assert-Check ($centered[0] -eq 0.0 -and $centered[1] -eq 0.0) `
        'A centered stick produced pointer movement.'
    # The curve reshapes speed without bending direction.
    $shapedDiagonal = Invoke-MouseDelta 0.7 0.7 0 'cubic' 100 100 0.01
    Assert-Check ([Math]::Abs($shapedDiagonal[0] + $shapedDiagonal[1]) -lt 0.0001) `
        'The response curve bent a diagonal push off its own axis.'

    # User contract: BetterJoy's own synthesized output must never satisfy a bind - a stick
    # bound to W cannot self-trigger a key_87 bind - and refcounting keeps one subsystem's
    # release from un-masking another's still-held output.
    $keyCode = 87
    $inputStateType.GetMethod('KeyDown').Invoke($null, @([int]$keyCode)) | Out-Null
    Assert-Check ([bool]$inputStateType.GetMethod('IsKeyHeld').Invoke($null, @([int]$keyCode))) `
        'A real key press was not reported as held.'
    $inputStateType.GetMethod('BeginSynthesizedKey').Invoke($null, @([int]$keyCode)) | Out-Null
    Assert-Check (-not [bool]$inputStateType.GetMethod('IsKeyHeld').Invoke($null, @([int]$keyCode))) `
        'A key BetterJoy is synthesizing still satisfied a bind.'
    $inputStateType.GetMethod('BeginSynthesizedKey').Invoke($null, @([int]$keyCode)) | Out-Null
    $inputStateType.GetMethod('EndSynthesizedKey').Invoke($null, @([int]$keyCode)) | Out-Null
    Assert-Check (-not [bool]$inputStateType.GetMethod('IsKeyHeld').Invoke($null, @([int]$keyCode))) `
        'One subsystem releasing a shared key un-masked the other subsystem still holding it.'
    $inputStateType.GetMethod('EndSynthesizedKey').Invoke($null, @([int]$keyCode)) | Out-Null
    Assert-Check ([bool]$inputStateType.GetMethod('IsKeyHeld').Invoke($null, @([int]$keyCode))) `
        'A real key press stayed masked after BetterJoy stopped synthesizing it.'
    $inputStateType.GetMethod('KeyUp').Invoke($null, @([int]$keyCode)) | Out-Null

    # User contract: the Sticks page's binds and options must be registered, or SetValue/
    # SetOptionValue reject the write and the pane silently fails to save.
    $keys = $mappingsType.GetField('Keys', [Reflection.BindingFlags]'Static,Public').GetValue($null)
    $optionKeys = $mappingsType.GetField('OptionKeys', [Reflection.BindingFlags]'Static,Public').GetValue($null)
    foreach ($bindKey in 'active_stick_mouse_left', 'active_stick_mouse_right',
                         'active_stick_keys_left', 'active_stick_keys_right',
                         'stick_left_key_up', 'stick_left_key_down',
                         'stick_left_key_left', 'stick_left_key_right',
                         'stick_right_key_up', 'stick_right_key_down',
                         'stick_right_key_left', 'stick_right_key_right') {
        Assert-Check ($keys -contains $bindKey) "Bind key $bindKey is not registered."
    }
    foreach ($optionKey in 'StickHoldToggle',
                           'StickMouseDeadzoneLeft', 'StickMouseDeadzoneRight',
                           'StickMouseSensitivityXLeft', 'StickMouseSensitivityYLeft',
                           'StickMouseSensitivityXRight', 'StickMouseSensitivityYRight',
                           'StickMouseCurveLeft', 'StickMouseCurveRight',
                           'StickKeysThresholdLeft', 'StickKeysThresholdRight',
                           'StickInhibitLeft', 'StickInhibitRight') {
        Assert-Check ($optionKeys -contains $optionKey) "Option key $optionKey is not registered."
    }

    # User contract: the directions default to W/A/S/D. Both default paths have to agree - Value()
    # falls through to LegacyValue, while middle-click-to-reset uses DefaultValue.
    $defaultValue = $mappingsType.GetMethod('DefaultValue', [Reflection.BindingFlags]'Static,Public')
    # LegacyValue rather than Value: Value() loads the on-disk profile store (AppPaths), while
    # LegacyValue IS the fallback path it ends at for a profile that never set this key.
    $legacyValue = $mappingsType.GetMethod('LegacyValue', $staticNonPublic)
    $expectedDefaults = @{
        'stick_left_key_up' = 'key_87'; 'stick_left_key_down' = 'key_83'
        'stick_left_key_left' = 'key_65'; 'stick_left_key_right' = 'key_68'
        'stick_right_key_up' = 'key_87'; 'stick_right_key_down' = 'key_83'
        'stick_right_key_left' = 'key_65'; 'stick_right_key_right' = 'key_68'
    }
    foreach ($entry in $expectedDefaults.GetEnumerator()) {
        Assert-Check ([string]$defaultValue.Invoke($null, @($entry.Key)) -eq $entry.Value) `
            "DefaultValue($($entry.Key)) was not $($entry.Value)."
        Assert-Check ([string]$legacyValue.Invoke($null, @($entry.Key)) -eq $entry.Value) `
            "LegacyValue($($entry.Key)) was not $($entry.Value) - it disagrees with DefaultValue."
    }
    # Activation defaults Disabled, so nothing about an existing profile changes until asked.
    foreach ($activationKey in 'active_stick_mouse_left', 'active_stick_keys_right') {
        Assert-Check ([string]$defaultValue.Invoke($null, @($activationKey)) -eq '0') `
            "$activationKey did not default to Disabled."
        Assert-Check ([string]$legacyValue.Invoke($null, @($activationKey)) -eq '0') `
            "$activationKey did not read as Disabled for a profile that never set it."
    }

    # User contract: SNES has no stick at all, and N64 and a solo Joy-Con have one - their right
    # stick block collapses rather than leaving a hole. A joined pair (no Kind) keeps both.
    $hasSticksPage = $reassignType.GetMethod('KindHasSticksPage', $staticNonPublic)
    $hasRightStick = $reassignType.GetMethod('KindHasRightStick', $staticNonPublic)
    Assert-Check (-not [bool]$hasSticksPage.Invoke($null, @([Enum]::Parse($kindType, 'Snes')))) `
        'The Sticks page was offered for a SNES pad, which has no stick.'
    foreach ($kindName in 'DualSense', 'DualShock4', 'Xbox', 'Pro', 'Left', 'N64') {
        Assert-Check ([bool]$hasSticksPage.Invoke($null, @([Enum]::Parse($kindType, $kindName)))) `
            "The Sticks page was unavailable for $kindName."
    }
    Assert-Check ([bool]$hasSticksPage.Invoke($null, @($null))) `
        'The Sticks page was unavailable with no controller selected.'
    foreach ($kindName in 'DualSense', 'DualShock4', 'Xbox', 'Pro') {
        Assert-Check ([bool]$hasRightStick.Invoke($null, @([Enum]::Parse($kindType, $kindName)))) `
            "$kindName lost its right stick block."
    }
    foreach ($kindName in 'N64', 'Left', 'Right', 'Snes') {
        Assert-Check (-not [bool]$hasRightStick.Invoke($null, @([Enum]::Parse($kindType, $kindName)))) `
            "$kindName was given a right stick block it does not have."
    }
    Assert-Check ([bool]$hasRightStick.Invoke($null, @($null))) `
        'A joined Joy-Con pair (no Kind) lost its right stick block.'

    Write-Host "Passed $checks stick output checks."
} finally {
    Pop-Location
}
