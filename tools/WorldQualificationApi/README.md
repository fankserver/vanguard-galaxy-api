# Empty-world qualification candidate

`make package-world-qualification CONFIGURATION=Release` builds the API sources in a separate project and writes only to `artifacts/WorldQualificationApi`. The DLL retains the loader identity `VGModAPI` but carries assembly metadata `VGModAPI.WorldQualification=empty-combat-v1`. It is a replacement candidate, not an additional plugin. Do not distribute it through the normal release pipeline or install both variants.

The normal solution and `make package` do not build this project. The candidate does not currently open world admission. A package marker is not run authorization, a verified sandbox, or native qualification. `RuntimeQualified` remains false.

Native execution requires an independently reviewed candidate, explicit authorization, an exclusive native lease, verified game/save/profile isolation and preservation, and bounded cleanup. A restricted first profile must reject executable world state before construction and publication, preserve ordinary identity/readiness/persistence checks, and exercise public creation plus automatic restoration. Passing that profile alone does not complete the broader world milestone.
