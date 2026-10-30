# Text to Outline for AutoCAD

**An AutoCAD 2020 plugin that turns text into clean outline polylines with one command: `TX`.**

Type any text in any TrueType font, at any height, then run `TX`. You get **closed polylines that trace
only the outer edges of the letters**: no overlapping lines, no internal lines, and holes
(o, a, e, B, 8…) kept as their own closed polylines.

![Before and after TX](docs/before-after.png)

---

## Why use it

Doing this by hand takes four steps, and they don't always give a clean result:

> **EXPLODE** the text → **OVERKILL** the duplicate lines → **delete** the internal lines by hand → **JOIN** the outer lines into polylines

`TX` does all four at once. It reads the exact letter shapes from the font and merges any overlapping
parts, which gives one closed polyline per edge:

![Exploded text vs. TX result](docs/manual-vs-plugin.png)

### Features

- ✅ Works with **TEXT** and **MTEXT** (multi-line text too)
- ✅ Any **TrueType font**: Arial, Times New Roman, Calibri, script fonts, bold and italic styles…
- ✅ Any **height, rotation, width factor, oblique angle, justification**, mirrored or upside-down text, any UCS
- ✅ Removes **overlaps and internal lines**, so touching letters become one shape
- ✅ Keeps **holes** as separate closed polylines (O = 2 polylines, B = 3)
- ✅ Outlines keep the text's **layer, color, linetype and lineweight**
- ✅ Choose to **keep or delete** the original text
- ✅ One **`U`** (undo) reverses the whole command
- ✅ No Express Tools needed

---

## Requirements

| | |
|---|---|
| AutoCAD | **AutoCAD 2020** (full version, 64-bit). Not AutoCAD LT. |
| Windows | Windows 10 or 11 |
| Fonts | The TrueType fonts your text uses must be installed |

---

## Step-by-step: install and use

### Step 1 — Download the plugin

**👉 [Download TextOutline.dll](https://github.com/lumidren/autocad-text-to-outline/raw/main/bin/TextOutline.dll)** (click it and the download starts)

If the link doesn't start a download, use one of these instead:

- **From the file page:** open [`bin/TextOutline.dll`](bin/TextOutline.dll) → click the **⬇ Download raw file** button
  at the top right of the file view.
- **Whole project as a ZIP:** on the [repository main page](https://github.com/lumidren/autocad-text-to-outline)
  click the green **`<> Code`** button → **Download ZIP** → right-click the ZIP → **Extract All…**.
  The plugin is in the `bin` folder. If you use the ZIP, **unblock the ZIP before extracting it** (Step 3),
  so all files inside are unblocked too.

> **Browser warning?** Browsers often warn about `.dll` files ("TextOutline.dll isn't commonly downloaded").
> In **Edge**: open Downloads (**Ctrl+J**) → hover the file → **⋯** → **Keep** → **Show more** → **Keep anyway**.
> In **Chrome**: open Downloads (**Ctrl+J**) → **Keep** / **Download suspicious file**.
>
> The file is the plugin built from the source code in this repository's `src` folder.

The file goes to your **Downloads** folder.

### Step 2 — Put it in a folder

1. Create a folder, for example:
   ```
   C:\AutoCAD Plugins\TextOutline
   ```
2. Move **`TextOutline.dll`** into that folder.

### Step 3 — Unblock the file (important!)

Windows blocks DLLs downloaded from the internet, and AutoCAD won't load a blocked file.

1. Right-click **`TextOutline.dll`** → **Properties**.
2. On the **General** tab, at the bottom, tick **☑ Unblock**.
3. Click **OK**.

> If you don't see an **Unblock** box, the file is already unblocked.

### Step 4 — Trust the folder in AutoCAD (recommended)

This stops the security warning from appearing every time the plugin loads.

1. Open AutoCAD and type **`OPTIONS`** → **Enter**.
2. Go to the **Files** tab.
3. Expand **Trusted Locations** → click **Add…** → **Browse…**.
4. Pick `C:\AutoCAD Plugins\TextOutline` → **OK** → **OK**.

### Step 5 — Load the plugin

1. Type **`NETLOAD`** → **Enter**.
2. Select **`TextOutline.dll`** → **Open**.
3. If a security dialog appears, click **Load**.
4. The command line confirms:
   ```
   Text to Outline loaded. Type TEXTOUTLINE (or TX) and select text.
   ```

> `NETLOAD` lasts until you close AutoCAD. To load the plugin automatically every time, see
> [Load automatically at startup](#load-automatically-at-startup).

### Step 6 — Convert your text

1. Write your text as usual with **`TEXT`** or **`MTEXT`**, using a TrueType font.
2. Type **`TX`** → **Enter**.
3. **Select the text.** Click it, drag a window around it, or type `ALL`. Then press **Enter**.
4. Answer the question:
   ```
   Delete original text? [Yes/No] <No>:
   ```
   - **Enter** or **N**: keep the text; the outlines are drawn on top of it.
   - **Y**: delete the text and keep only the outlines.
5. Done. The command line tells you what was made.

**What it looks like on the command line:**
```
Command: TX
Select text to convert to outline polylines: Specify opposite corner: 3 found
Select text to convert to outline polylines:
Delete original text? [Yes/No] <No>: Y
Created 24 outline polyline(s) from 3 text object(s).
```

> **Tip:** you can also select the text first and then type `TX`.
>
> **Undo:** type **`U`** once to remove the outlines and bring back the deleted text.

### Step 7 — Check the result

- Move the original text away (or choose **Y** to delete it) to see the outlines on their own.
- Click an outline. It is one **polyline**, and `LIST` shows it as **Closed**.
- The letter **H** gives 1 polyline, **O** gives 2 (outside + inside), **B** and **8** give 3.

---

## Commands

| Command | What it does |
|---|---|
| **`TX`** | Convert selected text to outline polylines (short name) |
| **`TEXTOUTLINE`** | Same as `TX` (full name) |
| **`TXQUALITY`** | Set how smooth curved letters are: **Draft**, **Normal** (default), **Fine** |

### `TXQUALITY` — curve smoothness

Curved edges (o, s, c…) are made of many short straight segments. Run `TXQUALITY` *before* `TX`:

| Option | Accuracy | Best for |
|---|---|---|
| **Draft** | 0.1 % of font size | Small text, light drawings |
| **Normal** | 0.02 % of font size | Everyday use (default) |
| **Fine** | 0.005 % of font size | Large signs, CNC / laser / plasma cutting |

---

## Load automatically at startup

1. In AutoCAD type `(findfile "acaddoc.lsp")` → **Enter**.
   - If it shows a file path, open that file with Notepad.
   - If it shows `nil`, create a new file **`acaddoc.lsp`** in `C:\AutoCAD Plugins\TextOutline`, and add
     that folder in **OPTIONS → Files → Support File Search Path → Add… → Browse…**.
2. Add this line to `acaddoc.lsp` (use **forward slashes** `/`):
   ```lisp
   (command "_.NETLOAD" "C:/AutoCAD Plugins/TextOutline/TextOutline.dll")
   ```
3. Save the file and restart AutoCAD. `TX` is now ready in every drawing.

---

## Tips

| You want to… | Do this |
|---|---|
| Fill the letters | `HATCH` → pattern **SOLID** → click inside each letter. Holes stay empty. |
| Cut letters on CNC / laser | `TXQUALITY` → **Fine**, then `TX`. All outlines are closed. |
| Make a border around letters | `OFFSET` the outline polylines |
| Make 3D letters | `EXTRUDE` the outer outlines, `EXTRUDE` the holes, then `SUBTRACT` the holes |
| Convert all text in a drawing | `TX` → type `ALL` → **Enter** |

---

## Limitations

| Can't convert | What to do |
|---|---|
| **SHX fonts** (txt.shx, simplex.shx, romans.shx…) | These letters are single lines with no thickness, so they have no outline. Change the text style to a TrueType font (`STYLE`), then run `TX`. The command line names any SHX text it skipped. |
| Text inside **blocks** | `EXPLODE` the block first |
| **Attributes, dimensions, tables, leaders** | Explode them to TEXT/MTEXT first |
| **AutoCAD LT** | LT cannot load plugins |

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `NETLOAD` fails / *"Cannot load assembly"* | The DLL is still blocked. Do [Step 3](#step-3--unblock-the-file-important), then restart AutoCAD. |
| *"Unknown command TX"* | The plugin isn't loaded. Run `NETLOAD` again ([Step 5](#step-5--load-the-plugin)). |
| `TX` runs a different command | Something else on your PC already uses `TX` (e.g. an alias in `acad.pgp`). Use **`TEXTOUTLINE`** instead. |
| *"Skipped … SHX fonts"* | That text uses an SHX font. Switch it to a TrueType font. |
| Curves look jagged on big text | `TXQUALITY` → **Fine**, then convert again. |
| Outline doesn't sit exactly on the text, or a *"proportions differ"* message | Please [open an issue](../../issues) with the font name, height, width factor and a screenshot. |

More detail: **[full user manual](docs/USER-MANUAL.md)**.

---

## For developers

<details>
<summary>How it works, building, and project layout</summary>

### How it works
1. Each text line is read with its style's TrueType font (bold/italic included).
2. Letter outlines come straight from the font (GDI+ `GraphicsPath.AddString`) and curves become short segments.
3. Contours are marked **solid** or **hole** from their winding direction.
4. AutoCAD **regions** merge them, largest first: solids united, holes subtracted. This removes overlaps
   and internal lines and keeps islands inside holes (like the R in ®).
5. The merged boundary is joined into closed loops, then fitted onto the text's real position, size, rotation,
   width factor, slant and plane, and written as closed `LWPOLYLINE`s.

### Building
No Visual Studio is needed. On a PC with AutoCAD 2020 installed, run:
```
build.bat
```
or pass the folder that contains `AcDbMgd.dll`, `AcCoreMgd.dll` and `AcMgd.dll`:
```
build.bat "D:\Autodesk\AutoCAD 2020"
```
The output is `bin\TextOutline.dll`.

### Testing without AutoCAD
`test\OutlineTest.cs` renders several fonts and checks, pixel by pixel, that the merged outline matches the
font's real shape ([result](docs/outline-test.png)). `test\MakeDocImages.cs` generates the pictures in `docs/`.

### Project layout
```
bin/TextOutline.dll     ready-to-use plugin for AutoCAD 2020
src/Commands.cs         AutoCAD commands, merging and polyline output
src/GlyphOutline.cs     reads letter outlines from TrueType fonts
test/                   geometry test and doc image generator
docs/USER-MANUAL.md     full user manual
build.bat               build script
```
</details>
