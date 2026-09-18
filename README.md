[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Reddit.Ads.Runners.OpenApiClient/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.Reddit.Ads.Runners.OpenApiClient/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Reddit.Ads.Runners.OpenApiClient/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.Reddit.Ads.Runners.OpenApiClient/actions/workflows/daily-automatic-update.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Reddit.Ads.Runners.OpenApiClient
### A runner that regenerates and updates Soenneker.Reddit.Ads.OpenApiClient.

This runner executes a GitHub action that updates another project. It's not meant for consumption.

## Local generation

From this repository, with the client repository alongside it:

```sh
dotnet run --project src/Soenneker.Reddit.Ads.Runners.OpenApiClient -- --Reddit:Ads:LocalDirectory=../soenneker.reddit.ads.openapiclient
```

The launch profile sets `ASPNETCORE_ENVIRONMENT=Local`. When launching the executable
directly, set that environment variable yourself.

Local mode downloads Reddit's OpenAPI document, normalizes it with Soenneker.OpenApi.Fixer,
regenerates the client with Kiota, and builds it without committing or pushing. It replaces
files in the client source directory, retaining the project file. The client repository
must already contain `src/Soenneker.Reddit.Ads.OpenApiClient/Soenneker.Reddit.Ads.OpenApiClient.csproj`.

Override `Reddit:Ads:ClientGenerationUrl` to use another specification URL. The default is
`https://ads-api.reddit.com/api/v3/openapi.json`.

Without `LocalDirectory`, the runner clones the GitHub client repository, regenerates,
builds, and commits/pushes using `GH__TOKEN`, `GIT__NAME`, and `GIT__EMAIL`.
A failed client build fails the run. Kiota currently omits the image creative asset's
parse factory; the runner adds a partial factory only when the generated model lacks it.
