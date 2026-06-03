import os
from PIL import Image, ImageDraw, ImageFilter

def generate_vector_icon(out_png, out_ico, out_ico_desktop):
    size = 256
    # Create main RGBA image
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    
    # Scale and translation parameters for initial high-res vector rendering
    scale = 2.2
    # Design bounds: width = 72.5, height = 90
    w_design = 72.5 * scale
    h_design = 90.0 * scale
    tx = (size - w_design) / 2.0 - 28.0 * scale
    ty = (size - h_design) / 2.0 - 9.0 * scale
    
    def transform(x, y):
        return (x * scale + tx, y * scale + ty)

    # 1. Draw R Logo Glow Backing
    glow_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow_layer)
    
    # R Logo coordinates
    r_bar_start = transform(28, 90)
    r_bar_end = transform(28, 18)
    r_top_line = transform(64, 18)
    r_mid_line = transform(28, 54)
    r_diag_start = transform(46, 54)
    r_diag_end = transform(72, 90)
    
    # Accent color for glow: vibrant neon blue (e.g. #0091FF)
    accent_rgb = (0, 145, 255)
    
    # Draw R outline on glow layer
    gd.line([r_bar_start, r_bar_end], fill=accent_rgb + (150,), width=16)
    gd.line([r_bar_end, r_top_line], fill=accent_rgb + (150,), width=16)
    arc_tl = transform(46, 18)
    arc_br = transform(82, 54)
    gd.arc([arc_tl, arc_br], start=270, end=90, fill=accent_rgb + (150,), width=16)
    gd.line([transform(64, 54), r_mid_line], fill=accent_rgb + (150,), width=16)
    gd.line([r_diag_start, r_diag_end], fill=accent_rgb + (150,), width=16)
    
    # Apply Gaussian Blur for realistic glow
    glow_blurred = glow_layer.filter(ImageFilter.GaussianBlur(8))
    img.alpha_composite(glow_blurred)
    
    # 2. Draw R Logo Sharp Elements
    sharp_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sharp_layer)
    
    # Draw sharp accent outline
    sd.line([r_bar_start, r_bar_end], fill=accent_rgb + (255,), width=8)
    sd.line([r_bar_end, r_top_line], fill=accent_rgb + (255,), width=8)
    sd.arc([arc_tl, arc_br], start=270, end=90, fill=accent_rgb + (255,), width=8)
    sd.line([transform(64, 54), r_mid_line], fill=accent_rgb + (255,), width=8)
    sd.line([r_diag_start, r_diag_end], fill=accent_rgb + (255,), width=8)
    
    # Draw sharp white core
    sd.line([r_bar_start, r_bar_end], fill=(255, 255, 255, 255), width=3)
    sd.line([r_bar_end, r_top_line], fill=(255, 255, 255, 255), width=3)
    sd.arc([arc_tl, arc_br], start=270, end=90, fill=(255, 255, 255, 255), width=3)
    sd.line([transform(64, 54), r_mid_line], fill=(255, 255, 255, 255), width=3)
    sd.line([r_diag_start, r_diag_end], fill=(255, 255, 255, 255), width=3)
    
    img.alpha_composite(sharp_layer)
    
    # 3. Draw Gradient Lightning Bolt
    # Create lightning mask
    lightning_mask = Image.new("L", (size, size), 0)
    lmd = ImageDraw.Draw(lightning_mask)
    
    # Lightning bolt vertices
    vertices = [
        transform(31.5 + 24, 9),
        transform(31.5 + 24, 58.5),
        transform(45.0 + 24, 58.5),
        transform(45.0 + 24, 99),
        transform(76.5 + 24, 45),
        transform(58.5 + 24, 45),
        transform(76.5 + 24, 9)
    ]
    lmd.polygon(vertices, fill=255)
    
    # Create linear gradient image
    gradient_img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd_draw = ImageDraw.Draw(gradient_img)
    
    # Draw gradient lines (top-left gold to bottom-right orange-red)
    for i in range(size * 2):
        t = i / (size * 2)
        if t < 0.5:
            factor = t / 0.5
            r = int(255)
            g = int(215 * (1 - factor) + 140 * factor)
            b = 0
        else:
            factor = (t - 0.5) / 0.5
            r = int(255)
            g = int(140 * (1 - factor) + 69 * factor)
            b = 0
            
        gd_draw.line([(i, 0), (0, i)], fill=(r, g, b, 255), width=2)
        
    # Mask gradient image with lightning bolt polygon
    lightning_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    lightning_layer.paste(gradient_img, (0, 0), mask=lightning_mask)
    
    # 4. Draw Lightning Glow & Border
    lightning_glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    lgd = ImageDraw.Draw(lightning_glow)
    lgd.polygon(vertices, outline=(255, 140, 0, 160), width=10)
    lightning_glow_blurred = lightning_glow.filter(ImageFilter.GaussianBlur(6))
    img.alpha_composite(lightning_glow_blurred)
    
    # Draw Lightning sharp white outline
    lightning_outline = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    lod = ImageDraw.Draw(lightning_outline)
    lod.polygon(vertices, outline=(255, 255, 255, 220), width=3)
    
    # Paste lightning bolt and its outline
    img.alpha_composite(lightning_layer)
    img.alpha_composite(lightning_outline)
    
    # 5. AUTO-CROP & RECENTER WITH TIGHT PADDING (6px)
    bbox = img.getbbox()
    if bbox:
        print(f"Original rendering bounding box: {bbox}")
        # Crop out the extra transparent margin
        cropped = img.crop(bbox)
        
        # Calculate target size with 6px safety padding on all sides
        padding = 6
        target_size = size - 2 * padding # 244
        
        w_cropped, h_cropped = cropped.size
        # Calculate scale ratio to fit the canvas while maintaining aspect ratio
        ratio = min(target_size / w_cropped, target_size / h_cropped)
        new_w = int(w_cropped * ratio)
        new_h = int(h_cropped * ratio)
        
        # Resize using high-quality Lanzcos filter
        resized = cropped.resize((new_w, new_h), Image.Resampling.LANCZOS)
        
        # Create a new, perfectly transparent 256x256 canvas
        centered_img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        
        # Offset to center the resized logo
        ox = (size - new_w) // 2
        oy = (size - new_h) // 2
        
        # Paste the cropped and scaled logo
        centered_img.paste(resized, (ox, oy))
        img = centered_img
        print(f"Recentered bounding box with 6px padding: {img.getbbox()}")
    else:
        print("Warning: Bounding box detection failed! No active pixels found.")

    # Save PNG target
    print(f"Saving vector PNG to: {out_png}")
    os.makedirs(os.path.dirname(out_png), exist_ok=True)
    img.save(out_png, "PNG")
    
    # Save ICO targets
    sizes = [16, 32, 48, 64, 128, 256]
    
    for ico_path in [out_ico, out_ico_desktop]:
        print(f"Saving vector ICO to: {ico_path}")
        os.makedirs(os.path.dirname(ico_path), exist_ok=True)
        img.save(ico_path, format="ICO", sizes=[(s, s) for s in sizes])
        
    print("Vector icon generation and optimization completed successfully!")

if __name__ == "__main__":
    # Determine project root based on the script location
    script_dir = os.path.dirname(os.path.abspath(__file__))
    proj_root = os.path.dirname(script_dir)
    
    out_png = os.path.join(proj_root, "Remex.Client", "Assets", "icon.png")
    out_ico = os.path.join(proj_root, "Remex.Client", "Assets", "icon.ico")
    out_ico_desktop = os.path.join(proj_root, "Remex.Client.Desktop", "icon.ico")
    generate_vector_icon(out_png, out_ico, out_ico_desktop)
