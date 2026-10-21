# Security policy

## Reporting a vulnerability

Report a vulnerability privately, by e-mail to **security@mentoragent.net**. Please do not open a public issue
for it.

Say which package and version you used, what you found and, if you can, how to reproduce it.

## Supported versions

Security fixes go to the latest minor release only.

| Version | Supported |
|---|---|
| The latest 1.1.x | ✅ |
| 1.0.x, release candidates and previews | ❌ |

## How a release is built

Every release is built by Novior's release pipeline, on hosted build agents, never on a developer's machine. The
pipeline runs in Novior's Azure DevOps and is not part of this repository. For each version it:

1. builds the library from a clean checkout and runs the whole test suite with the release checks on;
2. pushes the public snapshot of the source to [Novior-dev/MentorAgent](https://github.com/Novior-dev/MentorAgent)
   and tags it `v<version>`;
3. builds the five packages from a clean clone of that public snapshot, so each package's `.nuspec` names that
   commit and its SourceLink points to it on GitHub;
4. checks the packages before anything is published: five of them, each with the version being released, the
   public commit and its SBOM;
5. pushes them to nuget.org, with their symbols (`.snupkg`) to the NuGet symbol server, and attaches the same
   `.nupkg` files to the GitHub release `v<version>`.

Publishing to GitHub (steps 2 to 4) and to nuget.org (step 5) each waits for a person's approval.

To check a package you downloaded:

- **Its commit:** the `repository` element of the package's `.nuspec` has a `commit` attribute. It is the commit
  that the tag `v<version>` points to on GitHub.
- **Its signature:** the packages carry no author signature. nuget.org adds its repository signature to every
  package it serves, and `dotnet nuget verify --all <package>.nupkg` checks it.

### Reproducible builds

The same commit, built in the same folder, gives the same DLLs. Built in another folder, the two packages with
Razor components, `Novior.MentorAgent` and `Novior.MentorAgent.Abstractions`, differ only in the fields tied to
the PDB (its timestamp, the MVID and the PDB id), because the Razor compiler writes the path of the `.razor` files
into the PDB. The DLLs of the other three packages are the same byte for byte. Compare the DLLs, not the whole
`.nupkg`: its SBOM carries the date and a new namespace at every pack.

## Software bill of materials (SBOM)

From 1.1.0, each package carries its SBOM: an SPDX 2.2 document at `_manifest/spdx_2.2/manifest.spdx.json` inside
the `.nupkg`, with its SHA-256 next to it in `manifest.spdx.json.sha256`. Microsoft.Sbom.Targets writes it when
the package is packed; it is a build tool, not a dependency of the package. The document names Novior as the
supplier and lists:

- every file of the package, with its SHA-1 and SHA-256;
- the NuGet packages the build found.

The tool has two limits. The list of packages also has some that are used only to build: the SBOM tool itself,
and the previous version of the package, which the build compares the new one against. And it leaves out the
dependencies between the five MentorAgent packages, which the `.nuspec` lists.

You find it:

- in the NuGet global packages folder, after a restore:
  `~/.nuget/packages/novior.mentoragent/<version>/_manifest/spdx_2.2/manifest.spdx.json`
  (`%UserProfile%\.nuget\packages\…` on Windows), and the same for the other four packages;
- inside the `.nupkg` itself, which is a zip file, downloaded from nuget.org or from the GitHub release.
