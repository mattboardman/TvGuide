load("@rules_dotnet//dotnet:defs.bzl", "csharp_library", "csharp_nunit_test")

package(default_visibility = ["//visibility:public"])

csharp_library(
    name = "TvGuide",
    srcs = glob(["src/**/*.cs"]),
    target_frameworks = ["net9.0"],
    project_sdk = "web",
    internals_visible_to = ["TvGuide_test"],
    deps = [
        "@paket.tvguide_deps//jellyfin.controller",
        "@paket.tvguide_deps//jellyfin.model",
        "@paket.tvguide_deps//jellyfin.common",
        "@paket.tvguide_deps//jellyfin.data",
        "@paket.tvguide_deps//jellyfin.database.implementations",
        "@paket.tvguide_deps//microsoft.extensions.dependencyinjection.abstractions",
        "@paket.tvguide_deps//microsoft.extensions.logging.abstractions",
    ],
)

csharp_nunit_test(
    name = "TvGuide_test",
    srcs = glob(["test/**/*.cs"]),
    target_frameworks = ["net9.0"],
    deps = [
        ":TvGuide",
        "@paket.tvguide_deps//jellyfin.controller",
        "@paket.tvguide_deps//jellyfin.model",
    ],
)

filegroup(
    name = "plugin_meta",
    srcs = ["meta.json"],
)

