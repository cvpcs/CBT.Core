﻿using CBT.Core.Internal;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Versioning;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

// ReSharper disable PossibleNullReferenceException

namespace CBT.Core.UnitTests
{
    [TestFixture]
    public class ModulePropertyGeneratorTest
    {
        private readonly string _intermediateOutputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        private readonly IList<Tuple<string, string[]>> _moduleExtensions = new List<Tuple<string, string[]>>
        {
            new Tuple<string, string[]>("package2.thing", new[] {"before.package2.targets"}),
            new Tuple<string, string[]>("package3.a.b.c.d.e.f", new[] {"before.somethingelse.targets"}),
        };

        private readonly IList<PackageIdentity> _packages;
        private readonly string _packagesConfigPath;
        private readonly string _packagesPath;
        private readonly string _projectJsonPath;
        private readonly string _projectLockFilePath;

        public ModulePropertyGeneratorTest()
        {
            _packagesConfigPath = Path.Combine(_intermediateOutputPath, "packages.config");
            _projectJsonPath = Path.Combine(_intermediateOutputPath, "project.json");
            _projectLockFilePath = Path.Combine(_intermediateOutputPath, "project.lock.json");
            _packagesPath = Path.Combine(_intermediateOutputPath, "packages");

            _packages = new List<PackageIdentity>
            {
                new PackageIdentity("package1", new NuGetVersion("1.0.0")),
                new PackageIdentity("package1", new NuGetVersion("2.0.0")),
                new PackageIdentity("package2.thing", new NuGetVersion("2.5.1")),
                new PackageIdentity("package3.a.b.c.d.e.f", new NuGetVersion(10, 10, 9999, 9999, "beta99", "")),
            };

            // Have one module contain 200 extensions so we can test scalability
            //
            List<string> moduleExtensions = new List<string>();
            for (int i = 0; i < 100; i++)
            {
                moduleExtensions.Add($"before.package{i}.targets");
                moduleExtensions.Add($"after.package{i}.targets");
            }

            _moduleExtensions.Add(new Tuple<string, string[]>("package1", moduleExtensions.ToArray()));
        }

        [Test]
        public void ModulePropertiesAreCreatedPackagesConfig()
        {
            VerifyModulePropertiesAreCreated(_packagesConfigPath, package => $"{package.Id}.{package.Version}");
        }

        [Test]
        public void ModulePropertiesAreCreatedProjectJson()
        {
            VerifyModulePropertiesAreCreated(_projectJsonPath, package => $"{package.Id}{Path.DirectorySeparatorChar}{package.Version}");
        }

        [Test]
        public void NuGetPackagesConfigParserTest()
        {
            NuGetPackagesConfigParser configParser = new NuGetPackagesConfigParser();

            List<PackageIdentityWithPath> actualPackages = configParser.GetPackages(_packagesPath, _packagesConfigPath).ToList();

            actualPackages.ShouldBe(_packages);
        }

        [Test]
        public void NuGetProjectJsonParserTest()
        {
            NuGetProjectJsonParser configParser = new NuGetProjectJsonParser();

            List<PackageIdentityWithPath> actualPackages = configParser.GetPackages(_packagesPath, _projectJsonPath).ToList();

            actualPackages.ShouldBe(_packages);
        }

        [OneTimeTearDown]
        public void TestCleanup()
        {
            if (Directory.Exists(_intermediateOutputPath))
            {
                Directory.Delete(_intermediateOutputPath, true);
            }
        }

        [OneTimeSetUp]
        public void TestInitialize()
        {
            Directory.CreateDirectory(_packagesPath);

            // Write out a packages.config
            //
            new XDocument(
                new XDeclaration("1.0", "utf8", "yes"),
                new XComment("This file was auto-generated by unit tests"),
                new XElement("packages",
                    _packages.Select(i =>
                        new XElement("package",
                            new XAttribute("id", i.Id),
                            new XAttribute("version", i.Version)))
                )).Save(_packagesConfigPath);

            // Write out a project.json
            //
            File.WriteAllText(_projectJsonPath, $@"
                {{
                  ""dependencies"": {{
                    {String.Join($",{Environment.NewLine}    ", _packages.Select(i => $"\"{i.Id}\": \"{i.Version}\""))}
                  }},
                  ""frameworks"": {{
                    ""net45"": {{}}
                  }},
                  ""runtimes"": {{
                    ""win"": {{}}
                  }}
                }}");

            // Write out a project.lock.json file
            //
            new LockFileFormat().Write(_projectLockFilePath, new LockFile
            {
                Version = 1,
                Libraries = _packages.Select(i => new LockFileLibrary
                {
                    Name = i.Id,
                    Version = i.Version,
                }).ToList(),
            });
        }

        private void VerifyModulePropertiesAreCreated(string packageConfig, Func<PackageIdentity, string> packageFolderFunc)
        {
            // Write out a module.config for each module that has one
            //
            foreach (Tuple<string, string[]> moduleExtension in _moduleExtensions)
            {
                PackageIdentity package = _packages.First(i => i.Id.Equals(moduleExtension.Item1));

                string moduleConfigPath = Path.Combine(_packagesPath, packageFolderFunc(package), ModulePropertyGenerator.ModuleConfigPath);

                // ReSharper disable once AssignNullToNotNullAttribute
                Directory.CreateDirectory(Path.GetDirectoryName(moduleConfigPath));

                new XDocument(
                    new XDeclaration("1.0", "uf8", "yes"),
                    new XComment("This file was auto-generated by unit tests"),
                    new XElement("configuration",
                        new XElement("extensionImports",
                            moduleExtension.Item2.Select(i => new XElement("add", new XAttribute("name", i)))))
                ).Save(moduleConfigPath);
            }

            var logHelper = new CBTTaskLogHelper(new TestTask()
            {
                BuildEngine = new TestBuildEngine()
            });

            ModulePropertyGenerator modulePropertyGenerator = new ModulePropertyGenerator(logHelper, _packagesPath, packageConfig);

            string outputPath = Path.Combine(_intermediateOutputPath, "build.props");
            string extensionsPath = Path.Combine(_intermediateOutputPath, "Extensions");

            string[] importsBefore = { "before.props", "before2.props" };
            string[] importsAfter = { "after.props", "after2.props" };

            bool success = modulePropertyGenerator.Generate(outputPath, extensionsPath, importsBefore, importsAfter);

            success.ShouldBeTrue();

            File.Exists(outputPath).ShouldBeTrue();

            ProjectRootElement project = ProjectRootElement.Open(outputPath);

            project.ShouldNotBeNull();

            // Verify all properties
            //
            foreach (Tuple<string, string> item in new[]
            {
                new Tuple<string, string>("MSBuildAllProjects", "$(MSBuildAllProjects);$(MSBuildThisFileFullPath)"),
            }.Concat(_packages.Reverse().Distinct(new LambdaComparer<PackageIdentity>((x, y) => String.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase))).Select(i =>
                new Tuple<string, string>(
                    $"{ModulePropertyGenerator.PropertyNamePrefix}{i.Id.Replace(".", "_")}",
                    $"{ModulePropertyGenerator.PropertyValuePrefix}{packageFolderFunc(i)}"))))
            {
                ProjectPropertyElement propertyElement = project.Properties.FirstOrDefault(i => i.Name.Equals(item.Item1));

                propertyElement.ShouldNotBeNull();

                propertyElement.Value.ShouldBe(item.Item2);
            }

            List<string> expectedImports = importsBefore
                                                  .Concat(_packages.Select(i => $"{ModulePropertyGenerator.PropertyValuePrefix}{packageFolderFunc(i)}\\{ModulePropertyGenerator.ImportRelativePath}"))
                                                  .Concat(importsAfter).ToList();

            List<ProjectImportElement> actualImports = project.Imports.ToList();

            project.Imports.Count.ShouldBe(expectedImports.Count);

            for (int i = 0; i < expectedImports.Count; i++)
            {
                actualImports[i].Project.ShouldBe(expectedImports[i]);

                actualImports[i].Condition.ShouldBe($" Exists('{expectedImports[i]}') ");
            }

            // Verify module extensions were created
            //
            foreach (string item in _moduleExtensions.SelectMany(i => i.Item2))
            {
                string extensionPath = Path.Combine(extensionsPath, item);

                File.Exists(extensionPath).ShouldBeTrue();

                ProjectRootElement extensionProject = ProjectRootElement.Open(extensionPath);

                extensionProject.ShouldNotBeNull();

                extensionProject.Imports.Count.ShouldBe(_packages.Count);

                for (int i = 0; i < _packages.Count; i++)
                {
                    string importProject = $"{ModulePropertyGenerator.PropertyValuePrefix}{packageFolderFunc(_packages[i])}\\{ModulePropertyGenerator.ImportRelativePath}";
                    ProjectImportElement import = extensionProject.Imports.Skip(i).FirstOrDefault();

                    import.ShouldNotBeNull();

                    import.Project.ShouldBe(importProject);

                    import.Condition.ShouldBe($" Exists('{importProject}') ");
                }
            }
        }

        internal sealed class TestBuildEngine : IBuildEngine
        {
            // ReSharper disable once CollectionNeverQueried.Local
            private readonly IList<BuildEventArgs> _loggedBuildEvents = new List<BuildEventArgs>();

            public int ColumnNumberOfTaskNode => 0;

            public bool ContinueOnError => false;

            public int LineNumberOfTaskNode => 0;

            public string ProjectFileOfTaskNode => String.Empty;

            public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
            {
                throw new NotSupportedException();
            }

            public void LogCustomEvent(CustomBuildEventArgs e) => _loggedBuildEvents.Add(e);

            public void LogErrorEvent(BuildErrorEventArgs e) => _loggedBuildEvents.Add(e);

            public void LogMessageEvent(BuildMessageEventArgs e) => _loggedBuildEvents.Add(e);

            public void LogWarningEvent(BuildWarningEventArgs e) => _loggedBuildEvents.Add(e);
        }

        internal sealed class TestTask : ITask
        {
            public IBuildEngine BuildEngine { get; set; }

            public ITaskHost HostObject { get; set; }

            public bool Execute()
            {
                throw new NotSupportedException();
            }
        }
    }
}