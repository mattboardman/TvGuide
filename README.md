# TvGuide

A Jellyfin plugin that auto-generates virtual TV channels from your media library. Each genre in your library becomes a channel with a deterministic weekly schedule, giving you a classic channel-surfing experience. 

Works with all Jellyfin clients out of the box! 

!Note: Currently only works with jellyfin v10.11.5

![TvGuide in Jellyfin](static/tv_guide.png?raw=true)

## How it works

- Scans your library for movies and TV episodes and groups them by genre
- Creates a virtual Live TV channel per genre (Action, Comedy, Drama, etc.)
- Generates a deterministic weekly schedule using a seeded shuffle — the same week always produces the same lineup, so the guide stays consistent across devices
- Streams the current item at the correct seek position via FFmpeg concat, so you drop into whatever is "on" right now

## Build

Requires [Bazel](https://bazel.build/install) 9+.

```sh
bazel build //:TvGuide
```

The output DLL is at `bazel-bin/TvGuide/TvGuide.dll`.

## Install

1. Build the plugin (see above)
2. Copy the DLL and `meta.json` into your Jellyfin plugins directory:

```sh
mkdir -p /var/lib/jellyfin/data/plugins/TvGuide_1.0.0.0
cp bazel-bin/TvGuide/TvGuide.dll /var/lib/jellyfin/data/plugins/TvGuide_1.0.0.0/
cp meta.json /var/lib/jellyfin/data/plugins/TvGuide_1.0.0.0/
```

3. Restart Jellyfin
4. Go to **Dashboard > Scheduled Tasks > Live TV > Refresh Guide** and run it — your genre channels will appear under **Live TV > Guide**

## License

[MIT](LICENSE)
