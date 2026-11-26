# Novior.MentorAgent.Cli

The command line of Novior.MentorAgent. `novior-mentor scan` reads the pages of a running Blazor application and writes
the AutoMentor's `mentor-ui-manifest.json`, as Novior.MentorAgent.Scanner does at development time, for a CI pipeline
or a WebAssembly standalone app, where the scanner cannot run inside the app.

```bash
dotnet tool install --global Novior.MentorAgent.Cli
novior-mentor scan --url https://test.example.com --project src/Shop/Shop.csproj \
    --account Manager=SCAN_MANAGER_COOKIE --fail-on-change
```

The pages come from the app's built dll (the newest in the project's `bin`, or `--assembly`); the app runs at `--url`;
the browser, the Edge or Chrome installed, runs on the machine that runs the command, never on the app's server.

- `--account <role>=<variable>`: an account of the team with that role alone. The environment variable, a secret of
  the pipeline, holds its session cookie (`name=value`); it is set for the app's address only and never logged. A
  variable that is missing or empty stops the command.
- `--fail-on-change`: reads every page and writes nothing; exits with 3 and names the pages when the manifest would
  change.
- Exit codes: 0 done, 1 the scan could not run, 2 the command line is wrong, 3 the manifest would change.

It clicks with the accounts you give it: run it on a test environment, never on real data.

The full guide, with the options, the exit codes and the limits, is in the README of Novior.MentorAgent, section
«The scan of the pages».
