using System.Diagnostics;
using System.Globalization;
using System.Text;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class SyntheticMediaTests
{
	[Fact]
	public void Create_produces_a_ten_second_mp4_with_a_keyframe_every_second()
	{
		using var media = SyntheticMedia.Create();

		var bytes = media.ReadBytes();
		bytes.Length.ShouldBeGreaterThan(1024);
		Encoding.ASCII.GetString(bytes, 4, 4).ShouldBe("ftyp");

		var ffprobe = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe");
		var info = new ProcessStartInfo(ffprobe, $"-v error -select_streams v:0 -show_entries stream=codec_name,width,height,duration -of csv=p=0 \"{media.FilePath}\"")
		{
			RedirectStandardOutput = true,
			UseShellExecute = false,
		};
		using var process = Process.Start(info)!;
		var output = process.StandardOutput.ReadToEnd().Trim().Split(',');
		process.WaitForExit();

		output[0].ShouldBe("mpeg4");
		output[1].ShouldBe("64");
		output[2].ShouldBe("64");
		double.Parse(output[3], CultureInfo.InvariantCulture).ShouldBe(SyntheticMedia.DurationSeconds, 0.2);
	}
}
