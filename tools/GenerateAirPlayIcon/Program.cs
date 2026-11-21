using GenerateAirPlayIcon;

var toolDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var repoRoot = Path.GetFullPath(Path.Combine(toolDir, "..", ".."));

var svgPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(repoRoot, "AirPlay", "Resources", "tv_airplay_line.svg");

var icoPath = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(repoRoot, "AirPlay", "Resources", "airplay.ico");

var sizes = new[] { 16, 32, 48, 64, 128, 256 };
SvgToIco.Convert(svgPath, icoPath, sizes);
Console.WriteLine($"Wrote {icoPath} from {svgPath}");
