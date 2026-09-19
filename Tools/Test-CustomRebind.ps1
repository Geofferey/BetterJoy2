param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\BetterJoyForCemu\bin\x64\Release\BetterJoy2.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Configuration
Add-Type -AssemblyName System.Windows.Forms
[Configuration.ConfigurationManager]::AppSettings.Set('AHRS_beta', '0.1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
Push-Location (Split-Path -Parent $resolvedAssembly)
try {
    $assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
    $mappingsType = $assembly.GetType('BetterJoyForCemu.ControllerMappings', $true)
    $bindingType = $assembly.GetType('BetterJoyForCemu.ControllerMappings+CustomBinding', $true)
    $controllerType = $assembly.GetType('BetterJoyForCemu.Controller', $true)
    $reassignType = $assembly.GetType('BetterJoyForCemu.Reassign', $true)

    $validateInput = $mappingsType.GetMethod(
        'IsValidCustomBindingInput',
        [Reflection.BindingFlags]'Public,Static',
        $null,
        [Type[]]@([string], [bool]),
        $null)

    # User contract: ordinary custom binds still require a chord, while Rebind accepts one or
    # more normalized controller buttons.
    Assert-True (-not $validateInput.Invoke($null, @('joy_13', $false))) `
        'Rebind Disabled incorrectly accepted a one-button source.'
    Assert-True ($validateInput.Invoke($null, @('joy_13', $true))) `
        'Rebind Enabled did not accept a one-button source.'
    Assert-True ($validateInput.Invoke($null, @('joy_13+joy_15', $true))) `
        'Rebind Enabled did not accept a multi-button source.'

    $bindingConstructor = $bindingType.GetConstructor([Type[]]@([string], [string], [bool]))
    $typedBindings = [Array]::CreateInstance($bindingType, 1)
    $typedBindings.SetValue(
        $bindingConstructor.Invoke(@('joy_13', 'joy_15', $true)), 0)
    $serialize = $mappingsType.GetMethod(
        'SerializeCustomBindings', [Reflection.BindingFlags]'NonPublic,Static')
    $parse = $mappingsType.GetMethod(
        'ParseCustomBindings', [Reflection.BindingFlags]'NonPublic,Static')
    $serialized = [string]$serialize.Invoke($null, [object[]]@(,$typedBindings))
    Assert-True ($serialized -eq "joy_13`tjoy_15`t1") `
        'A Rebind Enabled row did not retain its mode in profile serialization.'
    $legacyBinding = $parse.Invoke($null, @("joy_13+joy_14`tjoy_15"))[0]
    Assert-True (-not $bindingType.GetProperty('Rebind').GetValue($legacyBinding)) `
        'A saved custom bind without a rebind field must remain Rebind Disabled.'

    $applyOverrides = $controllerType.GetMethod(
        'ApplyCustomButtonOverrides', [Reflection.BindingFlags]'NonPublic,Static')
    $buttonCount = [Enum]::GetValues($controllerType.GetNestedType('Button')).Count
    $isRebindHeld = $controllerType.GetMethod(
        'AreCustomRebindButtonsHeld',
        [Reflection.BindingFlags]'NonPublic,Static',
        $null,
        [Type[]]@([string], [bool[]], [bool[]]),
        $null)

    # A direct B rebind remains active when another button is also held, while every member of a
    # multi-button source must be physically present.
    [bool[]]$physical = New-Object bool[] $buttonCount
    $physical[13] = $true
    $heldArguments = New-Object object[] 3
    $heldArguments[0] = 'joy_13'
    $heldArguments[1] = $physical
    Assert-True ($isRebindHeld.Invoke($null, $heldArguments)) `
        'A physical B press did not activate its direct rebind.'
    $physical[14] = $true
    Assert-True ($isRebindHeld.Invoke($null, $heldArguments)) `
        'An additional held button incorrectly cancelled the B rebind.'
    $heldArguments[0] = 'joy_13+joy_15'
    Assert-True (-not $isRebindHeld.Invoke($null, $heldArguments)) `
        'A multi-button rebind activated before every source button was held.'

    # User contract: Rebind consumption removes the original normalized physical input before
    # normal physical-to-virtual mapping. Legacy continuous remaps remain additive here; Custom
    # bind controller outputs are tested separately as direct virtual-report buttons below.
    [bool[]]$output = New-Object bool[] $buttonCount
    [bool[]]$consumed = New-Object bool[] $buttonCount
    [bool[]]$remapped = New-Object bool[] $buttonCount
    $output[13] = $true
    $consumed[13] = $true
    $applyOverrides.Invoke($null, @($output, $consumed, $remapped))
    Assert-True (-not $output[13]) `
        'Rebind consumption did not remove the original physical B input.'

    # Regression: the pre-existing continuous physical remap path is unchanged.
    [bool[]]$output = New-Object bool[] $buttonCount
    [bool[]]$consumed = New-Object bool[] $buttonCount
    [bool[]]$remapped = New-Object bool[] $buttonCount
    $remapped[15] = $true
    $applyOverrides.Invoke($null, @($output, $consumed, $remapped))
    Assert-True ($output[15]) 'Legacy continuous remap output was no longer additive.'

    # User contract: joy_* in a Custom bind output is a direct virtual target. Position 15 is
    # Xbox X / PlayStation Square; it must never pass through the physical Y -> virtual X mapping
    # a second time, and the original virtual state remains additive when Rebind is Disabled.
    $xboxStateType = $assembly.GetType(
        'BetterJoyForCemu.VirtualOutput.OutputControllerXbox360InputState', $true)
    $psStateType = $assembly.GetType(
        'BetterJoyForCemu.VirtualOutput.OutputControllerDualShock4InputState', $true)
    $applyXboxVirtual = $controllerType.GetMethod(
        'ApplyCustomXboxVirtualButtons', [Reflection.BindingFlags]'NonPublic,Static')
    $applyPsVirtual = $controllerType.GetMethod(
        'ApplyCustomPlayStationVirtualButtons', [Reflection.BindingFlags]'NonPublic,Static')
    [bool[]]$virtualButtons = New-Object bool[] $buttonCount
    $virtualButtons[15] = $true
    $xboxState = [Activator]::CreateInstance($xboxStateType)
    $xboxState.a = $true
    $xboxResult = $applyXboxVirtual.Invoke($null, @($xboxState, $virtualButtons))
    Assert-True ($xboxResult.a -and $xboxResult.x -and -not $xboxResult.y) `
        'Virtual position 15 did not directly emit Xbox X while preserving existing Xbox A.'
    $psState = [Activator]::CreateInstance($psStateType)
    $psState.cross = $true
    $psResult = $applyPsVirtual.Invoke($null, @($psState, $virtualButtons))
    Assert-True ($psResult.cross -and $psResult.square -and -not $psResult.triangle) `
        'Virtual position 15 did not directly emit PlayStation Square while preserving Cross.'

    [bool[]]$virtualButtons = New-Object bool[] $buttonCount
    $virtualButtons[12] = $true
    $virtualButtons[19] = $true
    $xboxResult = $applyXboxVirtual.Invoke(
        $null, @([Activator]::CreateInstance($xboxStateType), $virtualButtons))
    Assert-True ($xboxResult.trigger_left -eq 255 -and $xboxResult.trigger_right -eq 255) `
        'Virtual LT/RT did not produce full Xbox trigger values.'
    $psResult = $applyPsVirtual.Invoke(
        $null, @([Activator]::CreateInstance($psStateType), $virtualButtons))
    Assert-True ($psResult.trigger_left -and $psResult.trigger_right -and
        $psResult.trigger_left_value -eq 255 -and $psResult.trigger_right_value -eq 255) `
        'Virtual L2/R2 did not produce digital and analog PlayStation trigger output.'

    # User contract: output assignment reads normalized physical state. Generated Y output must
    # never feed back through Controller.GetButton and make capture report Y instead of B.
    $proControllerType = $assembly.GetType('BetterJoyForCemu.ProController', $true)
    $controller = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(
        $proControllerType)
    [bool[]]$physicalButtons = New-Object bool[] $buttonCount
    [bool[]]$generatedButtons = New-Object bool[] $buttonCount
    $physicalButtons[13] = $true
    $generatedButtons[15] = $true
    $controllerType.GetField('buttons', [Reflection.BindingFlags]'Instance,NonPublic').SetValue(
        $controller, $physicalButtons)
    $controllerType.GetField(
        'continuousRemapButtons',
        [Reflection.BindingFlags]'Instance,NonPublic').SetValue($controller, $generatedButtons)
    $buttonType = $controllerType.GetNestedType('Button')
    Assert-True ($controller.GetButton([Enum]::ToObject($buttonType, 13))) `
        'Normalized capture lost the physical B input.'
    Assert-True (-not $controller.GetButton([Enum]::ToObject($buttonType, 15))) `
        'Generated Y output leaked into normalized controller capture.'

    # User contract: model-specific names are assigned directly to the canonical numeric button
    # code throughout the bindings UI. They are display labels only; stored joy_<code> values stay
    # unchanged. In particular, a right Joy-Con's physical face buttons occupy the canonical
    # DPAD slots, so those numeric codes must display as B/A/Y/X without an intermediate mapping.
    $kindType = $assembly.GetType('BetterJoyForCemu.ControllerKind', $true)
    $leftJoyCon = [Enum]::ToObject($kindType, 0)
    $rightJoyCon = [Enum]::ToObject($kindType, 1)
    $proController = [Enum]::ToObject($kindType, 2)
    $snesController = [Enum]::ToObject($kindType, 3)
    $n64Controller = [Enum]::ToObject($kindType, 4)
    $dualSense = [Enum]::ToObject($kindType, 5)
    $dualShock4 = [Enum]::ToObject($kindType, 6)

    # User contract: output recording starts from the normalized physical code and stores the
    # button produced by that controller's default layout. Dual-stick PlayStation Square remains
    # position 15 (Xbox X / PlayStation Square); sideways Joy-Con rotation is resolved once at
    # capture time rather than being deferred to output emission.
    $defaultVirtualButton = $reassignType.GetMethod(
        'DefaultVirtualButtonCode', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-True ($defaultVirtualButton.Invoke($null, @(15, $dualSense, 'dualsense:test')) -eq 15) `
        'DualSense Square did not record its default virtual west-face position.'
    Assert-True ($defaultVirtualButton.Invoke($null, @(0, $rightJoyCon, 'solo-right:test')) -eq 15) `
        'A sideways right Joy-Con B press did not record the default rotated virtual X position.'
    Assert-True ($defaultVirtualButton.Invoke($null, @(15, $null, 'pair:left+right')) -eq 15) `
        'A joined Joy-Con pair changed an already-normalized virtual button position.'

    # User contract: the same stored virtual position is presented for the selected Use-as
    # controller, never as another physical controller button. Position 15 is Xbox X,
    # DualShock/DualSense Square; position 13 is Xbox A, PlayStation Cross.
    $virtualDisplayName = $reassignType.GetMethod(
        'VirtualControllerButtonDisplayName', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-True ($virtualDisplayName.Invoke($null, @(15, 'xbox360')) -eq 'X') `
        'Virtual position 15 was not labeled Xbox X.'
    Assert-True ($virtualDisplayName.Invoke($null, @(13, 'xbox360')) -eq 'A') `
        'Virtual position 13 was not labeled Xbox A.'
    Assert-True ($virtualDisplayName.Invoke($null, @(15, 'dualshock4')) -eq 'SQUARE') `
        'Virtual position 15 was not labeled DualShock Square.'
    Assert-True ($virtualDisplayName.Invoke($null, @(6, 'dualsense_viiper')) -eq 'CREATE') `
        'Virtual position 6 was not labeled DualSense Create.'
    $virtualCodes = $reassignType.GetMethod(
        'VirtualControllerButtonCodes', [Reflection.BindingFlags]'NonPublic,Static')
    $xboxCodes = @($virtualCodes.Invoke($null, @('xbox360')))
    $playStationCodes = @($virtualCodes.Invoke($null, @('dualsense_viiper')))
    Assert-True ($xboxCodes -contains 15 -and $xboxCodes -notcontains 20 -and
        $xboxCodes -notcontains 25) `
        'Xbox output choices included physical-only buttons or omitted Xbox X.'
    Assert-True ($playStationCodes -contains 15 -and $playStationCodes -contains 20 -and
        $playStationCodes -notcontains 25) `
        'PlayStation output choices did not match the virtual controller surface.'

    $displayName = $reassignType.GetMethods([Reflection.BindingFlags]'NonPublic,Static') |
        Where-Object {
            $_.Name -eq 'ControllerButtonDisplayName' -and $_.GetParameters().Count -eq 2
        } | Select-Object -First 1
    $playStationLabels = @{
        15 = 'SQUARE'; 16 = 'TRIANGLE'; 14 = 'CIRCLE'; 13 = 'CROSS'
        11 = 'L1'; 18 = 'R1'; 12 = 'L2'; 19 = 'R2'; 10 = 'L3'; 17 = 'R3'
        7 = 'PS'; 6 = 'SHARE'; 8 = 'MENU'; 25 = 'MIC_MUTE'; 26 = 'FN1'; 27 = 'FN2'
        20 = 'TOUCHPAD'; 21 = 'TOUCHPAD_TAP'; 3 = 'DPAD_UP'
    }
    foreach ($entry in $playStationLabels.GetEnumerator()) {
        $actual = [string]$displayName.Invoke($null, @([int]$entry.Key, $dualSense))
        Assert-True ($actual -eq $entry.Value) `
            "PlayStation button code $($entry.Key) displayed '$actual' instead of '$($entry.Value)'."
    }
    $proLabels = @{ 11 = 'L'; 12 = 'ZL'; 18 = 'R'; 19 = 'ZR'; 10 = 'L STICK'; 17 = 'R STICK'; 13 = 'B' }
    foreach ($entry in $proLabels.GetEnumerator()) {
        Assert-True ($displayName.Invoke($null, @([int]$entry.Key, $proController)) -eq $entry.Value) `
            "Switch Pro button code $($entry.Key) did not use '$($entry.Value)'."
    }
    $rightJoyConLabels = @{
        0 = 'B'; 1 = 'A'; 2 = 'Y'; 3 = 'X'
        13 = 'DPAD_DOWN'; 14 = 'DPAD_RIGHT'; 15 = 'DPAD_LEFT'; 16 = 'DPAD_UP'
        11 = 'R'; 12 = 'ZR'; 18 = 'L'; 19 = 'ZL'; 10 = 'R STICK'; 17 = 'L STICK'
    }
    foreach ($entry in $rightJoyConLabels.GetEnumerator()) {
        Assert-True ($displayName.Invoke($null, @([int]$entry.Key, $rightJoyCon)) -eq $entry.Value) `
            "Right Joy-Con button code $($entry.Key) did not use '$($entry.Value)'."
    }
    Assert-True ($displayName.Invoke($null, @(0, $leftJoyCon)) -eq 'DPAD_DOWN') `
        'Left Joy-Con button code 0 must remain the physical D-pad Down button.'
    Assert-True ($displayName.Invoke($null, @(15, $null)) -eq 'Y') `
        'An unknown controller model must retain the canonical fallback label.'

    # User contract: on PlayStation layouts the Kenney glyph replaces the text label wherever the
    # imported set has one, including touchpad press/tap and the shared Kenney two-finger gesture
    # glyphs, and BetterJoy's own PS glyph; buttons without a glyph (Capture and SL/SR) keep their
    # text label, and unsupported controller layouts keep text entirely.
    $glyph = $reassignType.GetMethod('ControllerButtonGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    $sharedGlyphCodes = 0, 1, 2, 3, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21
    foreach ($code in $sharedGlyphCodes + 22, 23, 24, 25, 26, 27) {
        Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $dualSense))) `
            "DualSense button code $code has no embedded glyph."
    }
    foreach ($code in $sharedGlyphCodes + 22, 23, 24) {
        Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $dualShock4))) `
            "DualShock 4 button code $code has no embedded glyph."
    }
    foreach ($code in 4, 5, 9) {
        Assert-True ($null -eq $glyph.Invoke($null, @([int]$code, $dualSense))) `
            "Button code $code must fall back to its text label."
    }
    Assert-True ($null -eq $glyph.Invoke($null, @(13, $null))) `
        'An unknown controller model must keep text labels.'

    # Output glyphs follow Use as, independently of the physical controller selected on the left.
    # These are the attributed, unmodified Kenney Xbox Series glyphs (including Guide) and existing
    # Kenney PS art.
    $virtualGlyph = $reassignType.GetMethod(
        'VirtualControllerButtonGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    foreach ($code in @(0, 1, 2, 3, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19)) {
        Assert-True ($null -ne $virtualGlyph.Invoke($null, @([int]$code, 'xbox360'))) `
            "Xbox virtual button position $code has no embedded glyph."
    }
    foreach ($code in @(6, 8, 13, 14, 15, 16, 20)) {
        Assert-True ($null -ne $virtualGlyph.Invoke($null, @([int]$code, 'dualsense_viiper'))) `
            "DualSense virtual button position $code has no embedded glyph."
    }

    # User contract: Switch models use the unmodified Kenney glyph for the physical button
    # represented by each canonical code. Right Joy-Con face/D-pad slots therefore deliberately
    # use the inverse glyph families from Left/Pro. Capture uses the renamed, unmodified Kenney
    # generic circle glyph; SNES and N64 remain text until their own model-specific assets arrive.
    $switchGlyphCodes = 0..19
    foreach ($kind in @($leftJoyCon, $rightJoyCon, $proController)) {
        foreach ($code in $switchGlyphCodes) {
            Assert-True ($null -ne $glyph.Invoke($null, @([int]$code, $kind))) `
                "Switch controller $kind button code $code has no embedded glyph."
        }
    }
    Assert-True ($null -eq $glyph.Invoke($null, @(13, $snesController))) `
        'SNES bindings must remain text until SNES-specific assets are added.'
    Assert-True ($null -eq $glyph.Invoke($null, @(13, $n64Controller))) `
        'N64 bindings must remain text until N64-specific assets are added.'
    $glyphName = $reassignType.GetMethod(
        'ControllerButtonGlyphName', [Reflection.BindingFlags]'NonPublic,Static')
    $rightJoyConGlyphs = @{
        0 = 'switch_button_b'; 1 = 'switch_button_a'; 2 = 'switch_button_y'; 3 = 'switch_button_x'
        13 = 'switch_dpad_down'; 14 = 'switch_dpad_right'
        15 = 'switch_dpad_left'; 16 = 'switch_dpad_up'
        11 = 'switch_button_r'; 12 = 'switch_button_zr'
        18 = 'switch_button_l'; 19 = 'switch_button_zl'
        10 = 'switch_stick_r_press'; 17 = 'switch_stick_l_press'
        9 = 'switch_capture'
    }
    foreach ($entry in $rightJoyConGlyphs.GetEnumerator()) {
        $actual = [string]$glyphName.Invoke($null, @([int]$entry.Key, $rightJoyCon))
        Assert-True ($actual -eq $entry.Value) `
            "Right Joy-Con button code $($entry.Key) selected '$actual' instead of '$($entry.Value)'."
    }
    Assert-True ($glyphName.Invoke($null, @(0, $leftJoyCon)) -eq 'switch_dpad_down') `
        'Left Joy-Con button code 0 must select the D-pad Down glyph.'
    Assert-True ($glyphName.Invoke($null, @(13, $proController)) -eq 'switch_button_b') `
        'Switch Pro button code 13 must select the B glyph.'
    Assert-True ($glyphName.Invoke($null, @(22, $dualSense)) -eq 'touch_two') `
        'Two-finger tap must select the official Kenney two-finger glyph.'
    Assert-True ($glyphName.Invoke($null, @(23, $dualSense)) -eq 'touch_swipe_two_up') `
        'Two-finger scroll up must select the official Kenney upward swipe glyph.'
    Assert-True ($glyphName.Invoke($null, @(24, $dualSense)) -eq 'touch_swipe_two_down') `
        'Two-finger scroll down must select the official Kenney downward swipe glyph.'

    # User contract: standard keyboard keys use Mr. Breakfast's light keycaps directly from the
    # Windows virtual-key value. The full numpad reuses the corresponding digit/operator art
    # because this source does not provide separate numpad variants.
    $keyboardGlyphName = $reassignType.GetMethod(
        'KeyboardKeyGlyphName', [Reflection.BindingFlags]'NonPublic,Static')
    $keyboardGlyphs = @{
        8 = 'backspace_light'; 9 = 'tab_light'; 13 = 'return_light'
        16 = 'shift_light'; 17 = 'control_light'; 18 = 'alt_light'; 27 = 'escape_light'
        32 = 'space_text_light'; 37 = 'arrow_left_light'; 46 = 'delete_light'
        48 = '0_light'; 65 = 'a_key_light'; 67 = 'c_light'; 88 = 'x_key_light'
        91 = 'super_light'; 112 = 'f1_light'; 123 = 'f12_light'
        144 = 'num_light'; 145 = 'scroll_light'; 186 = ';_light'; 222 = "'_light"
    }
    foreach ($entry in $keyboardGlyphs.GetEnumerator()) {
        $actual = [string]$keyboardGlyphName.Invoke($null, @([int]$entry.Key))
        Assert-True ($actual -eq $entry.Value) `
            "Keyboard key code $($entry.Key) selected '$actual' instead of '$($entry.Value)'."
    }
    Assert-True ($null -eq $keyboardGlyphName.Invoke($null, @(135))) `
        'F24 must retain its text label because the selected set only includes F1-F12.'
    $numpadGlyphs = @{
        96 = '0_light'; 97 = '1_light'; 98 = '2_light'; 99 = '3_light'
        100 = '4_light'; 101 = '5_light'; 102 = '6_light'; 103 = '7_light'
        104 = '8_light'; 105 = '9_light'; 106 = 'asterisk_light'; 107 = '+_light'
        108 = ',_light'; 109 = '-_light'; 110 = '._light'; 111 = 'forward_slash_light'
    }
    foreach ($entry in $numpadGlyphs.GetEnumerator()) {
        $actual = [string]$keyboardGlyphName.Invoke($null, @([int]$entry.Key))
        Assert-True ($actual -eq $entry.Value) `
            "Numpad key code $($entry.Key) selected '$actual' instead of '$($entry.Value)'."
    }
    $keyboardGlyph = $reassignType.GetMethod(
        'KeyboardKeyGlyph', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-True ($null -ne $keyboardGlyph.Invoke($null, @(17))) `
        'The embedded Ctrl key glyph could not be loaded.'
    Assert-True ($null -eq $keyboardGlyph.Invoke($null, @(135))) `
        'Unsupported keyboard keys must not load an unrelated glyph.'

    # Every supported standard Windows key code must resolve to an embedded image. The Apps /
    # context-menu key remains textual because the selected source has no identifiable glyph.
    $standardKeyboardCodes = @(
        8, 9, 13, 16, 17, 18, 19, 20, 27, 32, 33, 34, 35, 36, 37, 38, 39, 40,
        44, 45, 46
    ) + @(48..57) + @(65..90) + @(91, 92) + @(96..111) + @(112..123) + @(
        144, 145, 160, 161, 162, 163, 164, 165, 186, 187, 188, 189, 190, 191,
        192, 219, 220, 221, 222, 226
    )
    Assert-True (($standardKeyboardCodes | Sort-Object -Unique).Count -eq 107) `
        'The standard keyboard regression inventory unexpectedly changed.'
    foreach ($keyCode in $standardKeyboardCodes) {
        Assert-True ($null -ne $keyboardGlyph.Invoke($null, @([int]$keyCode))) `
            "Supported standard key code $keyCode did not load its embedded glyph."
    }
    Assert-True ($null -eq $keyboardGlyphName.Invoke($null, @(93))) `
        'The Apps key must stay textual until an identifiable source glyph is available.'

    $labelParts = $reassignType.GetMethod('BindLabelParts', [Reflection.BindingFlags]'NonPublic,Static')
    $parts = $labelParts.Invoke($null, @('joy_7+joy_12', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -is [Drawing.Image] -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'PS + L2 must display as the PS glyph, +, and the L2 glyph.'
    $parts = $labelParts.Invoke($null, @('joy_9+joy_12', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -eq 'CAPTURE' -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'A button without a glyph must keep its text beside glyphs.'
    $parts = $labelParts.Invoke($null, @('key_17+key_18+key_46', $dualSense, $null))
    Assert-True ($parts.Count -eq 5 -and $parts[0] -is [Drawing.Image] -and
        $parts[1] -eq '+' -and $parts[2] -is [Drawing.Image] -and $parts[3] -eq '+' -and
        $parts[4] -is [Drawing.Image]) 'Ctrl + Alt + Delete must display as three keyboard glyphs.'

    # User contract: named Windows output presets on Custom binds use the same keyboard glyphs
    # as captured key_* outputs without changing their stable act_* stored values.
    $presetDisplayBinding = $mappingsType.GetMethod('CustomActionDisplayBinding')
    $presetBindings = @{
        'act_ctrl_alt_delete' = 'key_17+key_18+key_46'
        'act_ctrl_shift_escape' = 'key_17+key_16+key_27'
        'act_alt_tab_next' = 'key_18+key_9'
        'act_alt_shift_tab_previous' = 'key_18+key_16+key_9'
        'act_alt_tab_left' = 'key_18+key_9+key_37'
        'act_alt_tab_right' = 'key_18+key_9+key_39'
    }
    foreach ($entry in $presetBindings.GetEnumerator()) {
        $displayBinding = [string]$presetDisplayBinding.Invoke($null, @($entry.Key))
        Assert-True ($displayBinding -eq $entry.Value) `
            "$($entry.Key) selected '$displayBinding' instead of '$($entry.Value)'."
        $parts = $labelParts.Invoke($null, @($displayBinding, $dualSense, $null))
        Assert-True ($null -ne $parts -and ($parts | Where-Object { $_ -is [Drawing.Image] }).Count -gt 0) `
            "$($entry.Key) must resolve to keyboard glyphs on the Custom binds output button."
    }
    Assert-True ($null -eq $presetDisplayBinding.Invoke($null, @('act_media_play'))) `
        'Media presets must retain their text labels when no keyboard glyph sequence applies.'

    # User contract: use the available Mr. Breakfast media art on Custom bind outputs while
    # actions without matching art retain their text labels.
    $customActionParts = $reassignType.GetMethod(
        'CustomActionLabelParts', [Reflection.BindingFlags]'NonPublic,Static')
    $findCustomAction = $mappingsType.GetMethod('FindCustomAction')
    $pauseChoice = $findCustomAction.Invoke($null, @('act_media_pause'))
    Assert-True ($pauseChoice.DisplayGlyphNames.Count -eq 1 -and
        $pauseChoice.DisplayGlyphNames[0] -eq 'pause_symbolic_light') `
        'The Pause preset descriptor must select pause_symbolic_light.png.'

    # Every future preset that declares glyph metadata must automatically resolve to embedded art.
    foreach ($choice in $mappingsType.GetField('CustomActionChoices').GetValue($null)) {
        foreach ($glyphName in @($choice.DisplayGlyphNames)) {
            if ([String]::IsNullOrEmpty($glyphName)) { continue }
            $stream = $assembly.GetManifestResourceStream("InputPrompts.$glyphName.png")
            Assert-True ($null -ne $stream) `
                "$($choice.Value) declares missing glyph resource $glyphName.png."
            $stream.Dispose()
        }
    }
    foreach ($value in @('act_media_play', 'act_media_stop',
            'act_media_next', 'act_media_previous')) {
        $parts = $customActionParts.Invoke($null, @($value, $dualSense, $null))
        Assert-True ($parts.Count -eq 1 -and $parts[0] -is [Drawing.Image]) `
            "$value must display its available media glyph."
    }
    $pauseParts = $customActionParts.Invoke($null, @('act_media_pause', $dualSense, $null))
    $symbolicPauseStream = $assembly.GetManifestResourceStream(
        'InputPrompts.pause_symbolic_light.png')
    Assert-True ($null -ne $symbolicPauseStream) `
        'The attributed symbolic Pause resource was not embedded.'
    $symbolicPauseImage = [Drawing.Image]::FromStream($symbolicPauseStream)
    Assert-True ($pauseParts.Count -eq 1 -and $pauseParts[0] -is [Drawing.Image] -and
        $pauseParts[0].Width -eq $symbolicPauseImage.Width -and
        $pauseParts[0].Height -eq $symbolicPauseImage.Height) `
        'Media Pause must display pause_symbolic_light.png.'
    $symbolicPauseImage.Dispose()
    $symbolicPauseStream.Dispose()
    $parts = $customActionParts.Invoke($null, @('act_media_play_pause', $dualSense, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -is [Drawing.Image] -and
        $parts[1] -eq ' / ' -and $parts[2] -is [Drawing.Image]) `
        'Play / Pause must compose the separate Play and Pause glyphs.'
    foreach ($value in @('act_volume_up', 'act_volume_down', 'act_volume_mute')) {
        Assert-True ($null -eq $customActionParts.Invoke($null, @($value, $dualSense, $null))) `
            "$value must retain text because the selected source has no matching glyph."
    }

    # Exercise the actual Custom binds output-label path, not only its two helpers.
    # The application normally initializes AppPaths before this UI method runs. Keep this
    # isolated regression process on ControllerMappings' empty in-memory store so it never reads
    # or writes the user's real profile file.
    $mappingsType.GetField(
        'loaded', [Reflection.BindingFlags]'Static,NonPublic').SetValue($null, $true)
    $reassign = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($reassignType)
    $toolTip = New-Object Windows.Forms.ToolTip
    $reassignType.GetField(
        'tip_reassign', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($reassign, $toolTip)
    $splitButtonType = $assembly.GetType('BetterJoyForCemu.SplitButton', $true)
    $outputButton = [Activator]::CreateInstance($splitButtonType)
    $setPrettyName = $reassignType.GetMethod(
        'SetCustomBindingPrettyName', [Reflection.BindingFlags]'Instance,NonPublic')
    $labelPartsField = $splitButtonType.GetField(
        'labelParts', [Reflection.BindingFlags]'Instance,NonPublic')
    $setPrettyName.Invoke($reassign, @($outputButton, 'act_ctrl_alt_delete'))
    $outputParts = $labelPartsField.GetValue($outputButton)
    Assert-True ($outputParts.Count -eq 5 -and
        ($outputParts | Where-Object { $_ -is [Drawing.Image] }).Count -eq 3) `
        'The Custom binds output button did not render Ctrl + Alt + Delete as three glyphs.'
    $setPrettyName.Invoke($reassign, @($outputButton, 'act_media_play'))
    Assert-True ($labelPartsField.GetValue($outputButton).Count -eq 1) `
        'The Custom binds output button did not render the Play glyph.'
    $setPrettyName.Invoke($reassign, @($outputButton, 'act_volume_up'))
    Assert-True ($null -eq $labelPartsField.GetValue($outputButton) -and
        $outputButton.Text -eq 'Volume up') `
        'A text-only volume output retained stale media glyphs.'
    $outputButton.Dispose()
    $toolTip.Dispose()
    Assert-True ($null -eq $labelParts.Invoke($null, @('key_135', $dualSense, $null))) `
        'Unsupported keyboard-only binds must keep their plain text label.'
    $parts = $labelParts.Invoke($null, @('joy_0+joy_11', $rightJoyCon, $null))
    Assert-True ($parts.Count -eq 3 -and $parts[0] -is [Drawing.Image] -and $parts[1] -eq '+' -and
        $parts[2] -is [Drawing.Image]) 'Right Joy-Con B + R must display as two model-specific glyphs.'

    # User contract: the Joy-Con-only sections - rail buttons (SL/SR) on Bindings and Orientation
    # on Device behavior - show only for Joy-Cons. A joined pair reports no Kind, so an unknown
    # kind keeps them; every other controller type, including Pro, hides them.
    $railButtons = $reassignType.GetMethod(
        'KindIsJoyCon', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($railKindName in 'Left', 'Right') {
        Assert-True ([bool]$railButtons.Invoke($null, @([Enum]::Parse($kindType, $railKindName)))) `
            "The Joy-Con rail buttons section was hidden for a $railKindName Joy-Con."
    }
    Assert-True ([bool]$railButtons.Invoke($null, @($null))) `
        'A joined Joy-Con pair (no Kind) lost the Joy-Con rail buttons section.'
    foreach ($railKindName in 'Pro', 'Snes', 'N64', 'DualSense', 'DualShock4', 'Xbox') {
        Assert-True (-not [bool]$railButtons.Invoke($null, @([Enum]::Parse($kindType, $railKindName)))) `
            "The Joy-Con rail buttons section was shown for $railKindName."
    }

    # User contract: the Home LED option applies to Joy-Cons and the Pro Controller only - SNES and
    # N64 have no Home button, and PlayStation/Xbox pads have no such LED. A joined pair reports no
    # Kind, so an unknown kind keeps it.
    $homeLed = $reassignType.GetMethod(
        'KindHasHomeLed', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($homeLedKindName in 'Left', 'Right', 'Pro') {
        Assert-True ([bool]$homeLed.Invoke($null, @([Enum]::Parse($kindType, $homeLedKindName)))) `
            "The Home LED option was hidden for $homeLedKindName."
    }
    Assert-True ([bool]$homeLed.Invoke($null, @($null))) `
        'A joined Joy-Con pair (no Kind) lost the Home LED option.'
    foreach ($homeLedKindName in 'Snes', 'N64', 'DualSense', 'DualShock4', 'Xbox') {
        Assert-True (-not [bool]$homeLed.Invoke($null, @([Enum]::Parse($kindType, $homeLedKindName)))) `
            "The Home LED option was shown for $homeLedKindName."
    }

    # User contract: the hold-to-power-off label names the button that controller actually uses -
    # Capture on a solo left Joy-Con, PS on PlayStation pads, Guide on Xbox, Home otherwise
    # (including a joined pair, which reports no Kind).
    $powerOffName = $reassignType.GetMethod(
        'HomeLongPowerOffButtonName', [Reflection.BindingFlags]'Static,NonPublic')
    $powerOffNames = @{
        Left = 'Capture'; Right = 'Home'; Pro = 'Home'; Snes = 'Home'; N64 = 'Home'
        DualSense = 'PS'; DualShock4 = 'PS'; Xbox = 'Guide'
    }
    foreach ($powerOffEntry in $powerOffNames.GetEnumerator()) {
        $actualName = [string]$powerOffName.Invoke(
            $null, @([Enum]::Parse($kindType, $powerOffEntry.Key)))
        Assert-True ($actualName -eq $powerOffEntry.Value) `
            "$($powerOffEntry.Key) hold-to-power-off named '$actualName', expected '$($powerOffEntry.Value)'."
    }
    Assert-True (([string]$powerOffName.Invoke($null, @($null))) -eq 'Home') `
        'A joined Joy-Con pair (no Kind) must use the Home label.'

    # User contract: a profile section is made of rows discovered from the built layout, and each
    # row owns the gap above it. Collapsing a row whose items are all hidden therefore removes
    # exactly that row's height - no page reserves space for something it is not showing. A label
    # sitting a few pixels below its selector shares that selector's row rather than starting one.
    Add-Type -AssemblyName System.Windows.Forms
    $sectionType = $reassignType.GetNestedType('ProfileSection', [Reflection.BindingFlags]'NonPublic')
    $buildRows = $reassignType.GetMethod(
        'BuildSectionRows', [Reflection.BindingFlags]'Static,NonPublic')
    $section = [Activator]::CreateInstance($sectionType)
    $sectionType.GetField('BaselineTop').SetValue($section, 0)
    $sectionType.GetField('BaselineBottom').SetValue($section, 150)
    $sectionControls = $sectionType.GetField('Controls').GetValue($section)
    # Three rows, 39px apart, each a selector with its label offset 6px down beside it.
    foreach ($rowTop in 0, 39, 78) {
        $selector = New-Object Windows.Forms.Button
        $selector.SetBounds(114, $rowTop, 140, 31)
        $sectionControls.Add($selector)
        $rowLabel = New-Object Windows.Forms.Label
        $rowLabel.SetBounds(24, $rowTop + 6, 80, 17)
        $sectionControls.Add($rowLabel)
    }
    $buildRows.Invoke($null, @($section)) | Out-Null
    $rows = $sectionType.GetField('Rows').GetValue($section)
    Assert-True ($rows.Count -eq 3) `
        "A selector and its offset label must share one row: got $($rows.Count) rows, expected 3."
    $rowType = $reassignType.GetNestedType('ProfileRow', [Reflection.BindingFlags]'NonPublic')
    $rowTops = @($rows | ForEach-Object { [int]$rowType.GetField('BaselineTop').GetValue($_) })
    $rowHeights = @($rows | ForEach-Object { [int]$rowType.GetField('Height').GetValue($_) })
    Assert-True (($rowTops -join ',') -eq '0,39,78') `
        "Rows started at $($rowTops -join ','), expected 0,39,78."
    # The first two rows carry the 39px spacing; the last extends to the section's own bottom, so
    # the heights add up to the whole section and collapsing any row reclaims all of its space.
    Assert-True (($rowHeights -join ',') -eq '39,39,72') `
        "Row heights were $($rowHeights -join ','), expected 39,39,72."
    Assert-True ((($rowHeights | Measure-Object -Sum).Sum) -eq 150) `
        'Row heights must add up to the section height, or collapsing rows would drift.'

    # User contract: each control belongs to exactly one section, and sections tile - one ends
    # where the next begins. A divider introduces the section below it, so closing the previous
    # section past that divider handed the same control to both; reflow then moved it twice and
    # dragged content up across dividers.
    $layoutType = $reassignType.GetNestedType('PageLayout', [Reflection.BindingFlags]'NonPublic')
    $splitButtonType = $assembly.GetType('BetterJoyForCemu.SplitButton', $true)
    $anyInstance = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $layoutOwner = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($reassignType)
    foreach ($dictionaryField in 'pageSections', 'pageBottomPadding') {
        $field = $reassignType.GetField($dictionaryField, [Reflection.BindingFlags]'NonPublic,Instance')
        $field.SetValue($layoutOwner, [Activator]::CreateInstance($field.FieldType))
    }
    $layoutPage = New-Object Windows.Forms.Panel
    # Typed argument arrays, assigned element by element: an inline cast leaves PSObject wrappers
    # that reflection cannot bind to the real parameter types.
    $layoutArguments = New-Object object[] 3
    $layoutArguments[0] = $layoutOwner.PSObject.BaseObject
    $layoutArguments[1] = $layoutPage.PSObject.BaseObject
    $layoutArguments[2] = [int]96
    # ConstructorInfo.Invoke binds by position; Activator's binder refuses this nested private type.
    $layout = $layoutType.GetConstructors($anyInstance)[0].Invoke($layoutArguments)
    $headingMethod = $layoutType.GetMethod('Heading', $anyInstance)
    $rowMethod = $layoutType.GetMethod('Row', $anyInstance)
    $dividerMethod = $layoutType.GetMethod('Divider', $anyInstance)
    $finishMethod = $layoutType.GetMethod('Finish', $anyInstance)

    function Invoke-LayoutHeading([string]$Title, [string]$Description, $Key) {
        $headingArguments = New-Object object[] 3
        $headingArguments[0] = $Title
        $headingArguments[1] = $Description
        $headingArguments[2] = $Key
        $headingMethod.Invoke($layout, $headingArguments) | Out-Null
    }

    function Invoke-LayoutRow([string]$Text) {
        $rowArguments = New-Object object[] 6
        $rowArguments[0] = $null
        $rowArguments[1] = ([Activator]::CreateInstance($splitButtonType)).PSObject.BaseObject
        $rowArguments[2] = $Text
        $rowArguments[3] = [int]24
        $rowArguments[4] = [int]150
        $rowArguments[5] = [int]430
        $rowMethod.Invoke($layout, $rowArguments) | Out-Null
    }

    Invoke-LayoutHeading 'First' 'First section' $null
    Invoke-LayoutRow 'One'
    $dividerMethod.Invoke($layout, (New-Object object[] 0)) | Out-Null
    Invoke-LayoutHeading 'Second' 'Second section' 'gated'
    Invoke-LayoutRow 'Two'
    $finishArguments = New-Object object[] 1
    $finishArguments[0] = [int]0
    $finishMethod.Invoke($layout, $finishArguments) | Out-Null

    $builtSections = @($reassignType.GetField('pageSections',
        [Reflection.BindingFlags]'NonPublic,Instance').GetValue($layoutOwner)[$layoutPage])
    Assert-True ($builtSections.Count -eq 2) `
        "Each heading must open a section: got $($builtSections.Count), expected 2."
    $seenControls = New-Object 'Collections.Generic.List[object]'
    foreach ($builtSection in $builtSections) {
        foreach ($sectionControl in $sectionType.GetField('Controls').GetValue($builtSection)) {
            Assert-True (-not $seenControls.Contains($sectionControl)) `
                'A control belongs to two sections, so reflow would move it twice.'
            $seenControls.Add($sectionControl) | Out-Null
        }
    }
    $firstBottom = [int]$sectionType.GetField('DesignBottom').GetValue($builtSections[0])
    $secondTop = [int]$sectionType.GetField('DesignTop').GetValue($builtSections[1])
    Assert-True ($firstBottom -eq $secondTop) `
        "Sections must tile: first ends at $firstBottom, second starts at $secondTop."

    # User contract: the Home LED row is Nintendo-only, and Player LED is shown for the
    # Nintendo-protocol pads (a joined pair reports no Kind) plus the DualSense, which drives its
    # own player LEDs. Only DualShock 4 and Xbox lose the row.
    $nintendoKind = $reassignType.GetMethod(
        'KindIsNintendo', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($nintendoKindName in 'Left', 'Right', 'Pro', 'Snes', 'N64') {
        Assert-True ([bool]$nintendoKind.Invoke($null, @([Enum]::Parse($kindType, $nintendoKindName)))) `
            "$nintendoKindName was not treated as a Nintendo-protocol controller."
    }
    Assert-True ([bool]$nintendoKind.Invoke($null, @($null))) `
        'A joined Joy-Con pair (no Kind) was not treated as a Nintendo-protocol controller.'
    foreach ($nonNintendoKindName in 'DualSense', 'DualShock4', 'Xbox') {
        Assert-True (-not [bool]$nintendoKind.Invoke($null, @([Enum]::Parse($kindType, $nonNintendoKindName)))) `
            "$nonNintendoKindName was treated as a Nintendo-protocol controller."
    }

    # User contract: Global options > OpenRGB offers a Rescan dropdown with Enabled/Disabled, and
    # Disabled stops BetterJoy2 initiating an OpenRGB rescan at all. Enabled stays the default so
    # existing installs (which get the key seeded by AddMissingSettingsFromTemplate) keep the
    # long-standing nudge; only an explicit Disabled turns it off.
    $rescanType = $assembly.GetType('BetterJoyForCemu.OpenRgbRescan', $true)
    $rescanModes = $rescanType.GetField('Modes', [Reflection.BindingFlags]'Static,Public').GetValue($null)
    Assert-True ($rescanModes.Length -eq 2) 'The OpenRGB Rescan dropdown did not offer exactly two options.'
    Assert-True ($rescanModes[0].Item1 -eq 'Enabled' -and $rescanModes[0].Item2 -eq 'Enabled') `
        'Enabled was not the OpenRGB Rescan dropdown default (first) option.'
    Assert-True ($rescanModes[1].Item1 -eq 'Disabled' -and $rescanModes[1].Item2 -eq 'Disabled') `
        'Disabled was missing from the OpenRGB Rescan dropdown.'

    $rescanEnabled = $rescanType.GetMethod(
        'IsEnabledMode', [Reflection.BindingFlags]'Static,NonPublic')
    Assert-True (-not [bool]$rescanEnabled.Invoke($null, @('Disabled'))) `
        'OpenRGB Rescan set to Disabled still initiated a rescan.'
    Assert-True (-not [bool]$rescanEnabled.Invoke($null, @('disabled'))) `
        'A differently cased Disabled value still initiated an OpenRGB rescan.'
    foreach ($rescanValue in 'Enabled', '', 'something-else') {
        Assert-True ([bool]$rescanEnabled.Invoke($null, @($rescanValue))) `
            "OpenRGB rescan was suppressed for the non-Disabled value '$rescanValue'."
    }

    # The stored key has to be a known global option, or ApplicationSettings.SetValue refuses the
    # dropdown's write, and has to ship in App.config so existing configs get it seeded.
    $settingsType = $assembly.GetType('BetterJoyForCemu.ApplicationSettings', $true)
    $isGlobalOption = $settingsType.GetMethod(
        'IsGlobalOption', [Reflection.BindingFlags]'Static,Public')
    Assert-True ([bool]$isGlobalOption.Invoke($null, @('OpenRgbRescanMode'))) `
        'OpenRgbRescanMode is not registered as a global option.'
    $appConfig = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\BetterJoyForCemu\App.config') -Raw)
    $rescanSetting = $appConfig.configuration.appSettings.add |
        Where-Object { $_.key -eq 'OpenRgbRescanMode' }
    Assert-True ($null -ne $rescanSetting) 'App.config does not ship an OpenRgbRescanMode default.'
    Assert-True ($rescanSetting.value -eq 'Enabled') `
        'App.config shipped an OpenRgbRescanMode default other than Enabled.'

    # User contract: the DualSense audio DSP (power_save_control DisableAudio, 0x08) powers down
    # only while the mic is muted AND no output is wanted - Controller audio off, or Require
    # headphones with the jack empty. The DSP carries the mic too, so a live mic always keeps it
    # up, and the mic-mute bit (0x10) keeps its existing meaning either way.
    $dualSenseType = $assembly.GetType('BetterJoyForCemu.DualSenseController', $true)
    $powerSaveByte = $dualSenseType.GetMethod(
        'PowerSaveControlByte', [Reflection.BindingFlags]'Static,NonPublic')
    $powerSaveCases = @(
        @{ Muted = $true; Idle = $true; Expected = 0x18; Why = 'muted mic with no output wanted did not power the DSP down' },
        @{ Muted = $true; Idle = $false; Expected = 0x10; Why = 'the DSP powered down while an output was still wanted' },
        @{ Muted = $false; Idle = $true; Expected = 0x00; Why = 'the DSP powered down under a live microphone' },
        @{ Muted = $false; Idle = $false; Expected = 0x00; Why = 'power_save_control was set with the mic live and output wanted' }
    )
    foreach ($powerSaveCase in $powerSaveCases) {
        $actual = [int]$powerSaveByte.Invoke($null, @($powerSaveCase.Muted, $powerSaveCase.Idle))
        Assert-True ($actual -eq $powerSaveCase.Expected) `
            ("DualSense power_save_control: $($powerSaveCase.Why) " +
             "(got 0x{0:X2}, expected 0x{1:X2})." -f $actual, $powerSaveCase.Expected)
    }

    Write-Host 'Custom Rebind regression tests passed.'
} finally {
    Pop-Location
}
