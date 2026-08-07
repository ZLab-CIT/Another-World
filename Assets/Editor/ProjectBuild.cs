using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public static class ProjectBuild
{
    [MenuItem("Another World/Build/Windows")]
    public static void BuildWindows()
    {
        Build(BuildTarget.StandaloneWindows64,
            Path.Combine("Builds", "Windows", "AnotherWorld.exe"));
    }

    [MenuItem("Another World/Build/WebGL")]
    public static void BuildWebGL()
    {
        if (!BuildPipeline.IsBuildTargetSupported(
                BuildTargetGroup.WebGL, BuildTarget.WebGL))
            throw new BuildFailedException(
                "WebGL Build Support is not installed for this Unity editor.");

        Build(BuildTarget.WebGL, Path.Combine("Builds", "WebGL"));
    }

    private static void Build(BuildTarget target, string outputPath)
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
            .Select(scene => scene.path)
            .ToArray();
        if (scenes.Length == 0)
            throw new BuildFailedException("No enabled scenes are in Build Settings.");

        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(target == BuildTarget.WebGL
            ? fullOutputPath
            : Path.GetDirectoryName(fullOutputPath)
                ?? throw new InvalidOperationException("Invalid build path."));

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = fullOutputPath,
            target = target,
            options = BuildOptions.CleanBuildCache
        });

        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException(
                $"{target} build failed: {report.summary.result} "
                + $"({report.summary.totalErrors} errors). Review the Unity Editor log.");

        Console.WriteLine($"BUILD_SUCCEEDED target={target} path={fullOutputPath} "
            + $"size={report.summary.totalSize}");
    }
}
