using System;
using System.Configuration;
using System.Diagnostics;

namespace BetterJoyForCemu {
    // What's left of Joycon after step 5's NintendoController extraction: a thin pass-through
    // constructor plus the genuinely N64-only stick-drift-tracking tail (not shared Nintendo-
    // family content, deliberately left here rather than moved up - see
    // DOCS/CONTROLLERS-REFACTOR.md step 5 sub-step C). Splitting this into real leaf types
    // (JoyconController/ProController/SnesController/N64Controller) is a later sub-step; until
    // then this remains the sole concrete Nintendo-family type, same as before this extraction.
    public class Joycon : NintendoController {
        public Joycon(IntPtr handle_, bool imu, bool localize, float alpha, bool left, string path, string serialNum, int id = 0, bool isPro = false, bool isSnes = false, bool is64 = false, bool thirdParty = false)
            : base(handle_, imu, localize, alpha, left, path, serialNum, id, isPro, isSnes, is64, thirdParty) {
        }

        // 64 vars
        float maxX = 0.5f;
        float minX = -0.5f;
        float maxY = 0.5f;
        float minY = -0.5f;

        bool realn64Range = Boolean.Parse(ConfigurationManager.AppSettings["N64Range"]);

        private static float GetNormalizedValue(float value, float rawMin, float rawMax, float normalizedMin, float normalizedMax)
        {
            return (value - rawMin) / (rawMax - rawMin) * (normalizedMax - normalizedMin) + normalizedMin;
        }

        // internal, not private: called from Controller.MapToXbox360Input via an explicit Joycon
        // cast (N64 support is Nintendo-only, this stays Joycon-declared) - private wouldn't be
        // visible there even though Controller is Joycon's base, since this is the reverse
        // direction (base code calling a subclass-only member), which access modifiers alone
        // can't grant regardless of level short of internal/public.
        internal static float[] Getn64StickValues(Joycon input)
        {
            var isLeft = input.isLeft;
            var other = input.other;
            var stick = input.stick;
            var stick2 = input.stick2;
            var stick_correction = new float[] { 0f, 0f};

            var xAxis = (other == input && !isLeft) ? stick2[0] : stick[0];
            var yAxis = (other == input && !isLeft) ? stick2[1] : stick[1];


            if (xAxis < input.minX)
            {
                input.minX = xAxis;
            }

            if (xAxis > input.maxX)
            {
                input.maxX = xAxis;
            }

            if (yAxis < input.minY)
            {
                input.minY = yAxis;
            }

            if (yAxis > input.maxY)
            {
                input.maxY = yAxis;
            }

            var middleX = (input.minX + (input.maxX - input.minX)/2);
            var middleY = (input.minY + (input.maxY - input.minY)/2);
            #if DEBUG
            var desc = "";
            desc += "x: "+xAxis+"; y: "+yAxis;
            desc += "\n X: ["+input.minX+", "+input.maxX+"]; Y: ["+input.minY+", "+input.maxY+"] ";
            desc += "; middle ["+middleX+", "+middleY+"]";

            Debug.WriteLine(desc);
            #endif

            var negative_normalized = new float[] {-1, 0};
            var positive_normalized = new float[] {0, 1};

            var xRange = new float[] {-1f, 1f};
            var yRange = new float[] {-1f, 1f};

            if (input.realn64Range)
            {
                xRange = new float[] {-0.79f, 0.79f};
                yRange = new float[] {-0.79f, 0.79f};
            }


            if (xAxis < (middleX - middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, input.minX, (middleX - middleX), xRange[0], 0f);
            }

            if (xAxis > (middleX+middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, (middleX+middleX), input.maxX, 0f, xRange[1]);
            }

            if (yAxis < (middleY-middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, input.minY, (middleY-middleY), yRange[0], 0f);
            }

            if (yAxis > (middleY+middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, (middleY+middleY), input.maxY, 0f, yRange[1]);
            }


            return stick_correction;
        }

    }
}
