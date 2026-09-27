package com.clindsay94.remex.ui.components

import androidx.compose.material.icons.materialIcon
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes

/**
 * The Material "thermostat" glyph (Apache 2.0, Material Icons baseline) for the `pc.sensor` routine
 * trigger (routines spec 1.1). Built here from its path rather than taken from
 * material-icons-extended, which new code must not add usages of (RemEx-owdk). The fill is replaced
 * by the `Icon` tint wherever it is drawn.
 */
val ThermostatGlyph: ImageVector by lazy {
    materialIcon(name = "Filled.Thermostat") {
        addPath(
            pathData =
                addPathNodes(
                    "M15,13V5c0,-1.66 -1.34,-3 -3,-3S9,3.34 9,5v8c-1.21,0.91 -2,2.37 -2,4c0,2.76 2.24,5 5,5s5,-2.24 5,-5" +
                        "C17,15.37 16.21,13.91 15,13zM11,5c0,-0.55 0.45,-1 1,-1s1,0.45 1,1h-1v1h1v2h-1v1h1v2h-2V5z",
                ),
            fill = SolidColor(Color.Black),
        )
    }
}
