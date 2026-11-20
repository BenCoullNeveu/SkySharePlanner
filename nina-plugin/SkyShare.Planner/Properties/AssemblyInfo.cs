using System.Reflection;
using System.Runtime.InteropServices;

// NINA reads these attributes to build the plugin manifest shown in Options > Plugins.

[assembly: Guid("20e41444-d40b-44e2-bd2f-d2f6cb0dd9df")] // never change once published

[assembly: AssemblyTitle("SkyShare Planner")]
[assembly: AssemblyDescription("Builds the night's sequence at run time from a SkyShare plan file (targets, exposures, flats).")]
[assembly: AssemblyCompany("SkyShare")]
[assembly: AssemblyProduct("SkyShare Planner")]
[assembly: AssemblyCopyright("Copyright © 2026")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

// Lowest NINA build this plugin is compiled against (see .csproj)
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/BenCoullNeveu/SkySharePlanner")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/BenCoullNeveu/SkySharePlanner")]
[assembly: AssemblyMetadata("Tags", "Sequencer,Automation,Remote")]
[assembly: AssemblyMetadata("ChangelogURL", "")]
[assembly: AssemblyMetadata("FeaturedImageURL", "pack://application:,,,/SkyShare.Planner;component/Resources/SkyShareLogo.png")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Adds five sequencer items:

* SkyShare: Load Plan - finds tonight's *.plan.json in the plans folder, validates it and makes it available to the rest of the sequence.
* SkyShare Targets - container that builds one target block per plan entry, cloned from a PROTOTYPE you design in the template.
* SkyShare: Record Rotation - place inside the target PROTOTYPE after Center & Rotate; remembers the rotator position for flats.
* SkyShare Flats - container that builds the minimum set of trained flats for the targets that actually ran, grouped by orientation.
* SkyShare: Session Complete - last item; writes a session manifest (what was captured + what to process) that SkyStack picks up to start its pipeline.

Also: Health Check, Scheduler Preview (Target Scheduler), Darks. Settings that stay the same between sequences, a health check panel and a Restart button are on this options page.")]
