# Local Quest screenshots

Use this directory for headset captures during development. Screenshots can reveal the room,
computer displays, network names and other personal information. Image/date folders are
ignored; this guide is the only default tracked file in this directory.

## Pull a capture

With the Quest authorized for debugging:

```bash
adb devices -l
adb shell ls -lt /sdcard/Oculus/Screenshots
adb pull /sdcard/Oculus/Screenshots/REPLACE_WITH_FILENAME.jpg screenshots/
```

The listing is newest first. Use the actual displayed filename; the command does not select
or delete files automatically. Date subfolders are useful when comparing app revisions.

For a reproducible issue, keep the APK/source revision, headset OS, selected visualization
settings and relevant timing/tracking status alongside a sanitized image. Move an intentionally
shareable image to `docs/` and review it before committing. Ignoring an image does not erase it
from the working directory or from a commit if it was explicitly added earlier.
