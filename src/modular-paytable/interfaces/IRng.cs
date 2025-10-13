public interface IRNG
{
    /// <summary>
    /// Seed the RNG with a number or string.
    /// </summary>
    /// <param name="s">The seed value (number or string).</param>
    void Seed(object s);

    /// <summary>
    /// Draw a random number in range [0, 1).
    /// </summary>
    /// <returns>A random number between 0 (inclusive) and 1 (exclusive).</returns>
    double Random();

    /// <summary>
    /// Draw a number in range [lower, upper).
    /// </summary>
    /// <param name="lower">Lower bound (inclusive).</param>
    /// <param name="upper">Upper bound (exclusive).</param>
    /// <returns>A random number between lower (inclusive) and upper (exclusive).</returns>
    double Draw(double lower, double upper);
}
