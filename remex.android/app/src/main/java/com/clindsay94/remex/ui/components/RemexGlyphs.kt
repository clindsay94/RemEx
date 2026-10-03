package com.clindsay94.remex.ui.components

import androidx.compose.material.icons.materialIcon
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes

/**
 * Material glyphs (Apache 2.0, Material Icons baseline) built here from their paths rather than taken
 * from material-icons-extended, which new code must not add usages of (RemEx-owdk; phase 3 review
 * R2). The path data is the extended 1.7.8 artifact's own, read back from its compiled classes, so
 * each draws exactly as the icon it replaces. The fill is replaced by the `Icon` tint wherever drawn.
 */
private fun glyph(name: String, path: String, autoMirror: Boolean = false): ImageVector =
    materialIcon(name = name, autoMirror = autoMirror) {
        addPath(pathData = addPathNodes(path), fill = SolidColor(Color.Black))
    }

/** AutoMirrored.Filled.Undo: mirrored in right-to-left layouts, where "back in time" points right. */
val UndoGlyph: ImageVector by lazy {
    glyph(
        "AutoMirrored.Filled.Undo",
        "M12.5,8c-2.65,0 -5.05,0.99 -6.9,2.6L2,7v9h9l-3.62,-3.62c1.39,-1.16 3.16,-1.88 5.12,-1.88" +
            "c3.54,0 6.55,2.31 7.6,5.5l2.37,-0.78C21.08,11.03 17.15,8 12.5,8z",
        autoMirror = true,
    )
}

/** AutoMirrored.Filled.Redo. */
val RedoGlyph: ImageVector by lazy {
    glyph(
        "AutoMirrored.Filled.Redo",
        "M18.4,10.6C16.55,8.99 14.15,8 11.5,8c-4.65,0 -8.58,3.03 -9.96,7.22L3.9,16c1.05,-3.19 4.05,-5.5 7.6,-5.5" +
            "c1.95,0 3.73,0.72 5.12,1.88L13,16h9V7l-3.6,3.6z",
        autoMirror = true,
    )
}

/** Filled.DeleteSweep. */
val DeleteSweepGlyph: ImageVector by lazy {
    glyph(
        "Filled.DeleteSweep",
        "M15,16h4v2h-4zM15,8h7v2h-7zM15,12h6v2h-6zM3,18c0,1.1 0.9,2 2,2h6c1.1,0 2,-0.9 2,-2L13,8L3,8v10z" +
            "M14,5h-3l-1,-1L6,4L5,5L2,5v2h12z",
    )
}

/** Outlined.PushPin. */
val PushPinOutlinedGlyph: ImageVector by lazy {
    glyph(
        "Outlined.PushPin",
        "M14,4v5c0,1.12 0.37,2.16 1,3H9c0.65,-0.86 1,-1.9 1,-3V4H14M17,2H7C6.45,2 6,2.45 6,3c0,0.55 0.45,1 1,1" +
            "c0,0 0,0 0,0l1,0v5c0,1.66 -1.34,3 -3,3v2h5.97v7l1,1l1,-1v-7H19v-2c0,0 0,0 0,0c-1.66,0 -3,-1.34 -3,-3" +
            "V4l1,0c0,0 0,0 0,0c0.55,0 1,-0.45 1,-1C18,2.45 17.55,2 17,2L17,2z",
    )
}

/** Filled.Link. */
val LinkGlyph: ImageVector by lazy {
    glyph(
        "Filled.Link",
        "M3.9,12c0,-1.71 1.39,-3.1 3.1,-3.1h4L11,7L7,7c-2.76,0 -5,2.24 -5,5s2.24,5 5,5h4v-1.9L7,15.1" +
            "c-1.71,0 -3.1,-1.39 -3.1,-3.1zM8,13h8v-2L8,11v2zM17,7h-4v1.9h4c1.71,0 3.1,1.39 3.1,3.1s-1.39,3.1 -3.1,3.1" +
            "h-4L13,17h4c2.76,0 5,-2.24 5,-5s-2.24,-5 -5,-5z",
    )
}

/** Filled.LinkOff: a PC that no longer recognises this phone (sweep P3). */
val LinkOffGlyph: ImageVector by lazy {
    glyph(
        "Filled.LinkOff",
        "M17,7h-4v1.9h4c1.71,0 3.1,1.39 3.1,3.1c0,1.43 -0.98,2.63 -2.31,2.98l1.46,1.46C20.88,15.61 22,13.95 22,12" +
            "c0,-2.76 -2.24,-5 -5,-5zM16,11h-2.19l2,2L16,13zM2,4.27l3.11,3.11C3.29,8.12 2,9.91 2,12c0,2.76 2.24,5 5,5" +
            "h4v-1.9L7,15.1c-1.71,0 -3.1,-1.39 -3.1,-3.1c0,-1.59 1.21,-2.9 2.76,-3.07L8.73,11L8,11v2h2.73L13,15.27" +
            "L13,17h1.73l4.01,4L20,19.74L3.27,3L2,4.27z",
    )
}

/** Filled.Wifi. */
val WifiGlyph: ImageVector by lazy {
    glyph(
        "Filled.Wifi",
        "M1,9l2,2c4.97,-4.97 13.03,-4.97 18,0l2,-2C16.93,2.93 7.08,2.93 1,9zM9,17l3,3 3,-3c-1.65,-1.66 -4.34,-1.66 -6,0z" +
            "M5,13l2,2c2.76,-2.76 7.24,-2.76 10,0l2,-2C15.14,9.14 8.87,9.14 5,13z",
    )
}
