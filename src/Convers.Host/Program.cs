using Convers.Host;

// pdn-convers — the deployable convers app package (design.md src/Convers.Host). All composition
// lives in HostComposition.Build so the tests can boot the exact production wiring.

WebApplication app = HostComposition.Build(args);
app.Run();
