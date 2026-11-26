# Novior.MentorAgent.Scanner

Development-time scan of a Blazor application's pages for the AutoMentor of Novior.MentorAgent.

Reference it in Debug only, and register it under `#if DEBUG`, next to `AddMentorAgent()` with the AutoMentor on:

```xml
<PackageReference Include="Novior.MentorAgent.Scanner" Version="1.5.0" Condition="'$(Configuration)' == 'Debug'" />
```

```csharp
#if DEBUG
builder.Services.AddMentorUiScanner(options => options.Roles = ["Manager"]);
#endif
```

Once the application has started, in `Development` only, it opens each page in the Edge or Chrome already installed,
headless, and writes what the page offers to the project's `mentor-ui-manifest.json`, which the next build embeds.
Structure only, never a value. A page with route parameters is opened with the values of `RouteParameters`; the next
scan reads again only the pages whose code changed, and writes the manifest only when it changes.

`RouteParameters` is keyed by the route with each parameter's name only, since a configuration key cannot hold the
colon of a constraint. For the page `/orders/{id:int}`:

```json
{
  "MentorAgent": {
    "Scanner": {
      "RouteParameters": { "/orders/{id}": { "id": "1" } }
    }
  }
}
```

In a .NET MAUI Blazor Hybrid app on Windows, call `app.Services.StartMentorUiScan()` right after `builder.Build()`,
under `#if DEBUG`: the scan reads the pages in the app's own WebView2, as the app's user.

For a React or Angular front end on a server of its own, register the scanner in the .NET backend and give it the
front end's address: it reads the front end's pages instead of the host's, those of `FrontEndRoutes` or, with none,
those its links reach from its first page, with no user, each at every scan.

```json
{
  "MentorAgent": {
    "Scanner": {
      "FrontEndUrl": "http://localhost:5173/",
      "FrontEndRoutes": [ "/orders", "/orders/:id" ],
      "RouteParameters": { "/orders/{id}": { "id": "1" } }
    }
  }
}
```

The full guide, with the options and the limits of this version, is in the README of Novior.MentorAgent, section
«The scan of the pages».

## Safety

- It runs in `Development` only. In any other environment it does nothing, and `/_mentor/scan/start`, where its own
  user signs in, is not mapped. In a MAUI app it runs in a Debug build of the app only.
- It reads the pages of the application you are running, on the data of your development environment, never on real
  data. With `MentorAgent:Scanner:Roles` it reads them as a user of its own with each role, signed in with a one-time
  token from this machine only; that user stops working when the scan ends.
- It clicks only what opens a tab, a menu or a collapsed section, never a submit, a button of a form that posts, a link
  to another page, or a button that says delete, save, pay, send or confirm. Still, a click runs the application's own
  code: mark with `data-mentor-scan="skip"` anything that must not be clicked.
- In a MAUI app the scan clicks with the app's own user: use it only with a build that points to development data.
  The WebView's DevTools port stays open on the loopback until the app closes.
- While it scans, the browser's DevTools port is open on the loopback: a program running on this machine could connect
  to it. It closes when the scan ends, and the browser's profile is deleted. Disposed without being stopped, the
  application waits for the scan to close the browser, 15 seconds at most.
- If the application ends without stopping, for example killed by the debugger's Stop: on Windows the browser runs in
  a job object of its own and closes with the application. Where Windows refuses the job, the log says so. On Linux and
  macOS, and where the job was refused, the browser and its port stay open until the next scan starts, which closes
  the browser left by an application that has ended and deletes its profile, or until you close it. The profiles are
  in the temporary folder, named `mentor-ui-scan-*`, readable by your user only on Linux and macOS.
