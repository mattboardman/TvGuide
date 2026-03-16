"Generated"

load(":paket.tvguide_deps.bzl", _tvguide_deps = "tvguide_deps")

def _tvguide_deps_impl(module_ctx):
    _tvguide_deps()
    return module_ctx.extension_metadata(reproducible = True)

tvguide_deps_extension = module_extension(
    implementation = _tvguide_deps_impl,
)
