namespace DSPiConsole.Core.Models;

/// <summary>
/// Represents a single route in the matrix mixer (input -> output).
/// </summary>
public class MatrixRoute
{
    public int InputIndex { get; set; }   // 0=L, 1=R
    public int OutputIndex { get; set; }  // 0..N-1
    public bool Enabled { get; set; }
    public bool PhaseInvert { get; set; }
    public float Gain { get; set; }       // dB, -60 to +12
}

/// <summary>
/// Holds the full matrix mixer state for all input->output routes.
/// Routes are stored as [input * outputCount + output].
/// </summary>
public class MatrixMixerState
{
    public int OutputCount { get; }
    public MatrixRoute[] Routes { get; }

    public MatrixMixerState(int outputCount)
    {
        OutputCount = outputCount;
        Routes = new MatrixRoute[2 * outputCount]; // 2 inputs x N outputs
        for (int inp = 0; inp < 2; inp++)
        {
            for (int outp = 0; outp < outputCount; outp++)
            {
                Routes[inp * outputCount + outp] = new MatrixRoute
                {
                    InputIndex = inp,
                    OutputIndex = outp,
                    Enabled = false,
                    PhaseInvert = false,
                    Gain = 0f
                };
            }
        }
    }

    public MatrixRoute GetRoute(int input, int output)
    {
        return Routes[input * OutputCount + output];
    }
}
