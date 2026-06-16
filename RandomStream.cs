namespace SatisfactoryClusterCalculator;

public sealed class RandomStream
{
    private uint _state;

    public RandomStream(int seed)
    {
        _state = unchecked((uint)seed);
    }

    public float FRand()
    {
        return GetFraction();
    }

    public float FRandRange(float start, float end)
    {
        return start + ((end - start) * FRand());
    }

    private float GetFraction()
    {
        MutateSeed();
        return BitConverter.Int32BitsToSingle(unchecked((int)(0x3F800000u | (_state >> 9)))) - 1.0f;
    }

    private void MutateSeed()
    {
        _state = unchecked((_state * 196314165u) + 907633515u);
    }
}
