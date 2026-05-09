using System;
using System.IO;

namespace GymBeamShiftsControllerX.Tests;

internal static class TestPathHelper
{
    public static string GetWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GymBeamShiftsController.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Workspace root was not found.");
    }
}
