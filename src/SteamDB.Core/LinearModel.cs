namespace SteamDB.Core;

public sealed record LinearTerm(
    string Name,
    double Coefficient,
    double StandardError,
    double PValue,
    double Multiplier,
    double MultiplierLow,
    double MultiplierHigh,
    bool Starred,
    bool Doubling);

public sealed record LinearFit(double RSquared, int SampleSize, int DegreesOfFreedom, IReadOnlyList<LinearTerm> Terms);

public static class LinearModel
{
    public const int MinimumSample = 200;

    public static LinearFit? Fit(double[][] design, double[] target, IReadOnlyList<string> names, IReadOnlyList<bool> doubling)
    {
        if (design.Length != target.Length || design.Length == 0 || design[0].Length != names.Count)
            return null;
        var rows = design.Length;
        var cols = design[0].Length;
        if (rows <= cols + 1)
            return null;

        var xtx = new double[cols, cols];
        var xty = new double[cols];
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                xty[col] += design[row][col] * target[row];
                for (var other = 0; other < cols; other++)
                    xtx[col, other] += design[row][col] * design[row][other];
            }
        }

        if (!TryInvert(xtx, out var inverse))
            return null;
        var beta = new double[cols];
        for (var col = 0; col < cols; col++)
        {
            var sum = 0.0;
            for (var other = 0; other < cols; other++)
                sum += inverse[col, other] * xty[other];
            beta[col] = sum;
        }

        var mean = target.Average();
        var sse = 0.0;
        var sst = 0.0;
        for (var row = 0; row < rows; row++)
        {
            var predicted = 0.0;
            for (var col = 0; col < cols; col++)
                predicted += design[row][col] * beta[col];
            var residual = target[row] - predicted;
            sse += residual * residual;
            var centered = target[row] - mean;
            sst += centered * centered;
        }

        var df = rows - cols;
        var sigma2 = df > 0 ? sse / df : 0;
        var r2 = sst <= 0 ? 1 : 1 - sse / sst;
        var terms = new List<LinearTerm>(cols);
        for (var col = 0; col < cols; col++)
        {
            var se = Math.Sqrt(Math.Max(0, sigma2 * inverse[col, col]));
            var t = se == 0 ? 0 : beta[col] / se;
            var p = TwoTailedNormalP(t);
            var low = beta[col] - 1.96 * se;
            var high = beta[col] + 1.96 * se;
            double multiplier;
            double multiplierLow;
            double multiplierHigh;
            if (doubling[col])
            {
                multiplier = Math.Pow(2, beta[col]);
                multiplierLow = Math.Pow(2, low);
                multiplierHigh = Math.Pow(2, high);
            }
            else
            {
                multiplier = Math.Exp(beta[col]);
                multiplierLow = Math.Exp(low);
                multiplierHigh = Math.Exp(high);
            }

            var starred = multiplierLow > 1 || multiplierHigh < 1;
            terms.Add(new LinearTerm(names[col], beta[col], se, p, multiplier, multiplierLow, multiplierHigh, starred, doubling[col]));
        }

        return new LinearFit(Math.Clamp(r2, 0, 1), rows, df, terms);
    }

    public static double TwoTailedNormalP(double t)
    {
        var z = Math.Abs(t) / Math.Sqrt(2);
        return Math.Clamp(Erfc(z), 0, 1);
    }

    private static bool TryInvert(double[,] matrix, out double[,] inverse)
    {
        var n = matrix.GetLength(0);
        var aug = new double[n, n * 2];
        for (var row = 0; row < n; row++)
        {
            for (var col = 0; col < n; col++)
                aug[row, col] = matrix[row, col];
            aug[row, n + row] = 1;
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            var best = Math.Abs(aug[col, col]);
            for (var row = col + 1; row < n; row++)
            {
                var value = Math.Abs(aug[row, col]);
                if (value > best)
                {
                    best = value;
                    pivot = row;
                }
            }

            if (best < 1e-12)
            {
                inverse = new double[n, n];
                return false;
            }

            if (pivot != col)
            {
                for (var k = 0; k < n * 2; k++)
                    (aug[col, k], aug[pivot, k]) = (aug[pivot, k], aug[col, k]);
            }

            var div = aug[col, col];
            for (var k = 0; k < n * 2; k++)
                aug[col, k] /= div;
            for (var row = 0; row < n; row++)
            {
                if (row == col)
                    continue;
                var factor = aug[row, col];
                if (factor == 0)
                    continue;
                for (var k = 0; k < n * 2; k++)
                    aug[row, k] -= factor * aug[col, k];
            }
        }

        inverse = new double[n, n];
        for (var row = 0; row < n; row++)
        {
            for (var col = 0; col < n; col++)
                inverse[row, col] = aug[row, n + col];
        }

        return true;
    }

    private static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.5 * z);
        var ans = t * Math.Exp(-z * z - 1.26551223 +
            t * (1.00002368 +
            t * (0.37409196 +
            t * (0.09678418 +
            t * (-0.18628806 +
            t * (0.27886807 +
            t * (-1.13520398 +
            t * (1.48851587 +
            t * (-0.82215223 +
            t * 0.17087277)))))))));
        return x >= 0 ? ans : 2 - ans;
    }
}
