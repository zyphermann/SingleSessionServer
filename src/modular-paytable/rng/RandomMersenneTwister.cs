using System;

public class RandomMersenneTwister : IRNG
{
    private const int N = 624;
    private const int M = 397;
    private const uint MATRIX_A = 0x9908b0dfU;
    private const uint UPPER_MASK = 0x80000000U;
    private const uint LOWER_MASK = 0x7fffffffU;

    private uint[] mt = new uint[N];
    private int mti = N + 1;

    public RandomMersenneTwister(int? seed = null)
    {
        if (!seed.HasValue)
            seed = (int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Seed((uint)seed);
    }

    public void Seed(object s)
    {
        uint seed;
        if (s is int intSeed)
        {
            seed = (uint)intSeed;
        }
        else if (s is uint uintSeed)
        {
            seed = uintSeed;
        }
        else if (s is string strSeed)
        {
            seed = (uint)strSeed.GetHashCode();
        }
        else
        {
            throw new ArgumentException("Seed must be an int or string.");
        }

        Seed(seed);
    }

    private void Seed(uint s)
    {
        mt[0] = s;
        for (mti = 1; mti < N; mti++)
        {
            uint prev = mt[mti - 1] ^ (mt[mti - 1] >> 30);
            mt[mti] =
                (uint)((((prev & 0xffff0000) >> 16) * 1812433253U) << 16)
                + ((prev & 0x0000ffff) * 1812433253U)
                + (uint)mti;
        }
    }

    public void SeedByArray(uint[] init_key)
    {
        int i = 1,
            j = 0;
        int key_length = init_key.Length;
        Seed(19650218U);
        int k = (N > key_length ? N : key_length);
        for (; k > 0; k--)
        {
            uint prev = mt[i - 1] ^ (mt[i - 1] >> 30);
            mt[i] = (uint)(
                (
                    mt[i]
                    ^ ((((prev & 0xffff0000) >> 16) * 1664525U) << 16)
                        + ((prev & 0x0000ffff) * 1664525U)
                )
                + init_key[j]
                + (uint)j
            );
            i++;
            j++;
            if (i >= N)
            {
                mt[0] = mt[N - 1];
                i = 1;
            }
            if (j >= key_length)
                j = 0;
        }

        for (k = N - 1; k > 0; k--)
        {
            uint prev = mt[i - 1] ^ (mt[i - 1] >> 30);
            mt[i] = (uint)(
                (
                    mt[i]
                    ^ ((((prev & 0xffff0000) >> 16) * 1566083941U) << 16)
                        + ((prev & 0x0000ffff) * 1566083941U)
                ) - (uint)i
            );
            i++;
            if (i >= N)
            {
                mt[0] = mt[N - 1];
                i = 1;
            }
        }

        mt[0] = 0x80000000U;
    }

    public uint RandomInt31()
    {
        return RandomInt32() >> 1;
    }

    public uint RandomInt32()
    {
        uint y;
        uint[] mag01 = { 0x0U, MATRIX_A };

        if (mti >= N)
        {
            int kk;

            if (mti == N + 1)
                Seed(5489U);

            for (kk = 0; kk < N - M; kk++)
            {
                y = (mt[kk] & UPPER_MASK) | (mt[kk + 1] & LOWER_MASK);
                mt[kk] = mt[kk + M] ^ (y >> 1) ^ mag01[y & 0x1U];
            }

            for (; kk < N - 1; kk++)
            {
                y = (mt[kk] & UPPER_MASK) | (mt[kk + 1] & LOWER_MASK);
                mt[kk] = mt[kk + (M - N)] ^ (y >> 1) ^ mag01[y & 0x1U];
            }

            y = (mt[N - 1] & UPPER_MASK) | (mt[0] & LOWER_MASK);
            mt[N - 1] = mt[M - 1] ^ (y >> 1) ^ mag01[y & 0x1U];

            mti = 0;
        }

        y = mt[mti++];

        // Tempering
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c5680U;
        y ^= (y << 15) & 0xefc60000U;
        y ^= y >> 18;

        return y;
    }

    public double Random()
    {
        return RandomInt32() * (1.0 / 4294967296.0);
    }

    public double Draw(double lower, double upper)
    {
        return lower + Random() * (upper - lower);
    }

    public double RandomReal1()
    {
        return RandomInt32() * (1.0 / 4294967295.0);
    }



    public double RandomReal3()
    {
        return (RandomInt32() + 0.5) * (1.0 / 4294967296.0);
    }

    public double RandomRes53()
    {
        uint a = RandomInt32() >> 5;
        uint b = RandomInt32() >> 6;
        return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
    }

    public int Draw(int lower, int upper)
    {
        if (lower > upper)
        {
            int temp = lower;
            lower = upper;
            upper = temp;
        }

        int interval = upper - lower;
        if (interval == 0)
            return lower;

        return lower + (int)(RandomInt32() % (interval + 1));
    }

    public static void PrintHistogram()
    {
        var rng = new RandomMersenneTwister(42);
        int[] buckets = new int[10];
        int trials = 100_000;

        for (int i = 0; i < trials; i++)
        {
            double r = rng.Random();
            int index = (int)(r * 10);
            if (index == 10)
                index = 9;
            buckets[index]++;
        }

        for (int i = 0; i < buckets.Length; i++)
        {
            Console.WriteLine($"{i / 10.0:F1}–{(i + 1) / 10.0:F1}: {buckets[i]}");
        }
    }
}
