namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal static class KolmogorovSmirnov
{
	public static double Statistic(IReadOnlyList<double> a, IReadOnlyList<double> b)
	{
		var x = a.OrderBy(v => v).ToArray();
		var y = b.OrderBy(v => v).ToArray();
		int i = 0, j = 0;
		var d = 0.0;
		while (i < x.Length && j < y.Length)
		{
			var v = Math.Min(x[i], y[j]);
			while (i < x.Length && x[i] <= v)
			{
				i++;
			}

			while (j < y.Length && y[j] <= v)
			{
				j++;
			}

			d = Math.Max(d, Math.Abs((double)i / x.Length - (double)j / y.Length));
		}

		return d;
	}

	public static double Coefficient(double alpha) => Math.Sqrt(-Math.Log(alpha / 2) / 2);

	public static double CriticalValue(double alpha, int n, int m) =>
		Coefficient(alpha) * Math.Sqrt((n + m) / ((double)n * m));
}
