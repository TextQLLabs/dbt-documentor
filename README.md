# DBT Documentor
![logo](logo.png)

Check out the demo video [here](https://www.youtube.com/watch?v=BGB0D1G10FE)!

Automatically generate docs for undocumented DBT models.

Contact me at mark %at-sign% textql.com, or book a meeting with me [here](https://zcal.co/i/KwB9yGkh).

## Installation

The dotnet SDK version 6.0 is needed to install this project. `cd` into the project directory, `dotnet publish`, and install the binary into your `PATH`. To build a self-contained binary (not dependent on dotnet), run `dotnet publish --sc --runtime {RID}` where the RID is a valid runtime identifier [link](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog). Popular RIDs are `linux-x64`, `osx.13-x64`, `osx.13-arm64`, `win10-x64`.

TODO: Publish binaries for popular platforms to npm/pip.

## Usage
```
USAGE: DbtHelper [--help] [--working-directory <path>] [--gen-undocumented] [--gen-specific [<models list>...]]

OPTIONS:

    --working-directory <path>
                          DBT project root (default: .)
    --gen-undocumented    Generate docs for all undocumented models (default: enabled, disabled by --gen-specific)
    --gen-specific [<models list>...]
                          Generate docs only for specified model names (comma-separated list) (default: none, disabled by --gen-undocumented)
    --help                display this list of options.
```
### Examples
Generate docs for all undocumented models:
```
$ DbtHelper --working-directory /home/mark/src/dbt_project
```
Generate docs for a specific model in dry run:
```
$ DbtHelper --working-directory /home/mark/src/dbt_project --gen-specific fct_orders --dry-run
```

## Tips/Bugs
- This works great with the [dbt-docs](https://github.com/dbt-labs/dbt-docs) package to automatically create and propagate docs and YAML.
- Make sure to run from a clean working directory; review the outputs and re-run or clean up if there are issues.
- Comments tend to get deleted in modified YAML :(. Reach out if you need this fixed!