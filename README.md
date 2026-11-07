# vATIS-Sync

Syncs real-world D-ATIS info into VATSIM's [vATIS](https://vatis.app) client automatically.

vATIS-Sync reads current D-ATIS broadcasts from [atis.info](https://atis.info), cleans them up for use on VATSIM,
and fills the *Airport Conditions* and *NOTAMs* fields of your D-ATIS stations in vATIS through its local API.
It keeps them fresh while you control.

This project is in active development.

## Credits

This project grew out of [glott/vATISLoad](https://github.com/glott/vATISLoad), a Python script by glott that
introduced the idea of syncing real D-ATIS into vATIS. vATIS-Sync is a separate rewrite in C#. Thanks to glott for the original.

atis.info provides the D-ATIS data. vATIS is by the vATIS developers.

## Disclaimer

Unofficial. Not affiliated with or endorsed by VATSIM, vATIS or atis.info. Real-world D-ATIS text is for
simulation use only and must never be used for real navigation.
