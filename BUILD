load("@rules_dotnet//dotnet:defs.bzl", "csharp_library", "csharp_nunit_test")

package(default_visibility = ["//visibility:public"])

csharp_library(
    name = "TvGuide",
    srcs = glob(["src/**/*.cs"]),
    resources = ["Configuration/config.html"],
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
        "@paket.tvguide_deps//openai",
        "@paket.tvguide_deps//system.clientmodel",
        "@paket.tvguide_deps//system.memory.data",
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

# DLLs not provided by the Jellyfin host that must be bundled with the plugin.
genrule(
    name = "runtime_deps",
    srcs = [
        "@paket.tvguide_deps//openai:files",
        "@paket.tvguide_deps//system.clientmodel:files",
        "@paket.tvguide_deps//system.memory.data:files",
    ],
    outs = ["runtime_deps_out/OpenAI.dll", "runtime_deps_out/System.ClientModel.dll", "runtime_deps_out/System.Memory.Data.dll"],
    cmd = " && ".join([
        "cp $$(echo $(locations @paket.tvguide_deps//openai:files) | tr ' ' '\\n' | grep 'lib/net6.0/OpenAI.dll') $(location runtime_deps_out/OpenAI.dll)",
        "cp $$(echo $(locations @paket.tvguide_deps//system.clientmodel:files) | tr ' ' '\\n' | grep 'lib/net9.0/System.ClientModel.dll') $(location runtime_deps_out/System.ClientModel.dll)",
        "cp $$(echo $(locations @paket.tvguide_deps//system.memory.data:files) | tr ' ' '\\n' | grep 'lib/net9.0/System.Memory.Data.dll') $(location runtime_deps_out/System.Memory.Data.dll)",
    ]),
)
