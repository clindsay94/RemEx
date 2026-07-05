import os
from PIL import Image, ImageDraw

def extract_premium_3d_logo(src_path, out_png, out_ico, out_ico_desktop, proj_root):
    if not os.path.exists(src_path):
        print(f"Error: Master asset not found at {src_path}")
        return False
        
    print(f"Loading master 3D brand asset from: {src_path}")
    img = Image.open(src_path).convert("RGBA")
    width, height = img.size
    
    # 1. Adaptively sample the background color from the top-left corner
    bg_samples = []
    for cy in range(15):
        for cx in range(15):
            bg_samples.append(img.getpixel((cx, cy))[:3])
            
    avg_bg_r = sum(c[0] for c in bg_samples) / len(bg_samples)
    avg_bg_g = sum(c[1] for c in bg_samples) / len(bg_samples)
    avg_bg_b = sum(c[2] for c in bg_samples) / len(bg_samples)
    print(f"Sampled background color: ({avg_bg_r:.2f}, {avg_bg_g:.2f}, {avg_bg_b:.2f})")
    
    # Create target canvas of the same size with full transparency
    logo_img = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    
    # We scan y < 68% height to fully exclude the "RemEx" text at the bottom
    scan_height = int(height * 0.68)
    
    src_pixels = img.load()
    logo_pixels = logo_img.load()
    
    left, top, right, bottom = width, height, 0, 0
    
    # 2. Extract alpha channel based on pixel deviation from background
    for y in range(scan_height):
        for x in range(width):
            r, g, b, a = src_pixels[x, y]
            
            # Compute difference from background
            dr = max(0, r - avg_bg_r)
            dg = max(0, g - avg_bg_g)
            db = max(0, b - avg_bg_b)
            
            diff_max = max(dr, dg, db)
            diff_min = min(dr, dg, db)
            sat = diff_max - diff_min
            val = 0.299 * dr + 0.587 * dg + 0.114 * db
            
            # Metric combining luminance change and color saturation
            metric = val + sat * 1.5
            
            # Threshold to filter out background carbon fiber noise
            if metric > 15:
                # Soft-edge alpha mapping: fully transparent under 15, fully opaque above 50
                alpha = int(min(255, max(0, (metric - 15) / (50 - 15) * 255)))
                
                # Boost color brightness slightly to maintain contrast on transparent background
                factor = 1.05
                nr = min(255, int(r * factor))
                ng = min(255, int(g * factor))
                nb = min(255, int(b * factor))
                
                logo_pixels[x, y] = (nr, ng, nb, alpha)
                
                # Build active bounding box (ignoring very soft glow edges for tighter fit)
                if alpha > 35:
                    if x < left: left = x
                    if x > right: right = x
                    if y < top: top = y
                    if y > bottom: bottom = y
            else:
                logo_pixels[x, y] = (0, 0, 0, 0)
                
    # 3. Crop and Center inside a 256x256 icon canvas
    if left < right and top < bottom:
        print(f"Logo bounding box detected: ({left}, {top}, {right}, {bottom})")
        cropped = logo_img.crop((left, top, right + 1, bottom + 1))
        w_crop = right - left + 1
        h_crop = bottom - top + 1
        
        # Fit inside 256x256 with 6px safety padding
        padding = 6
        target_size = 256 - 2 * padding # 244
        
        ratio = min(target_size / w_crop, target_size / h_crop)
        new_w = int(w_crop * ratio)
        new_h = int(h_crop * ratio)
        
        # Resize using LANCZOS downsampling for sharp metallic details
        resized = cropped.resize((new_w, new_h), Image.Resampling.LANCZOS)
        
        final_img = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
        ox = (256 - new_w) // 2
        oy = (256 - new_h) // 2
        final_img.paste(resized, (ox, oy))
        
        # Save PNG target
        print(f"Saving transparent 3D PNG to: {out_png}")
        os.makedirs(os.path.dirname(out_png), exist_ok=True)
        final_img.save(out_png, "PNG")
        
        # Save ICO targets
        sizes = [16, 32, 48, 64, 128, 256]
        for ico_path in [out_ico, out_ico_desktop]:
            print(f"Saving transparent 3D ICO to: {ico_path}")
            os.makedirs(os.path.dirname(ico_path), exist_ok=True)
            final_img.save(ico_path, format="ICO", sizes=[(s, s) for s in sizes])
            
        # 4. Generate Android Adaptive Icon Foreground Layers
        print("\nGenerating Android multi-density adaptive icon foregrounds...")
        android_res_path = os.path.join(proj_root, "remex.android", "app", "src", "main", "res")
        
        # Build conflict safety: rename old XML foreground to prevent name duplicate conflicts
        old_vector_fg = os.path.join(android_res_path, "drawable", "ic_launcher_foreground.xml")
        if os.path.exists(old_vector_fg):
            backup_vector_fg = os.path.join(android_res_path, "drawable", "ic_launcher_foreground_vector.xml")
            print(f"Renaming old vector foreground to avoid conflicts: {backup_vector_fg}")
            if os.path.exists(backup_vector_fg):
                os.remove(backup_vector_fg)
            os.rename(old_vector_fg, backup_vector_fg)
            
        densities = {
            "mdpi": 108,
            "hdpi": 162,
            "xhdpi": 216,
            "xxhdpi": 324,
            "xxxhdpi": 432
        }
        
        for density, s in densities.items():
            # Target size of logo is 60% of viewport size S to fit within the 66dp safe zone
            target_s = int(s * 0.60)
            
            ratio_a = min(target_s / w_crop, target_s / h_crop)
            new_wa = int(w_crop * ratio_a)
            new_ha = int(h_crop * ratio_a)
            
            resized_logo_a = cropped.resize((new_wa, new_ha), Image.Resampling.LANCZOS)
            
            android_fg = Image.new("RGBA", (s, s), (0, 0, 0, 0))
            ox_a = (s - new_wa) // 2
            oy_a = (s - new_ha) // 2
            android_fg.paste(resized_logo_a, (ox_a, oy_a))
            
            out_path = os.path.join(android_res_path, f"drawable-{density}", "ic_launcher_foreground.png")
            print(f"Saving Android foreground ({density}, {s}x{s}) to: {out_path}")
            os.makedirs(os.path.dirname(out_path), exist_ok=True)
            android_fg.save(out_path, "PNG")
            
        print("\nPremium 3D icon assets generated successfully for both Desktop and Android!")
        return True
    else:
        print("Error: Could not isolate 3D logo from the master brand asset.")
        return False

if __name__ == "__main__":
    # Path to the high-fidelity master brand asset generated by the user's session
    master_path = r"C:\Users\Connor\.gemini\antigravity-cli\brain\264a3c7f-b818-4ea7-8c6f-4ae2e4b15d7b\remex_premium_logo_1780392763742.png"
    
    # Determine project root based on the script location
    script_dir = os.path.dirname(os.path.abspath(__file__))
    proj_root = os.path.dirname(script_dir)
    
    out_png = os.path.join(proj_root, "remex.desktop", "Assets", "icon.png")
    out_ico = os.path.join(proj_root, "remex.desktop", "Assets", "icon.ico")
    out_ico_desktop = os.path.join(proj_root, "remex.agent", "icon.ico")
    
    extract_premium_3d_logo(master_path, out_png, out_ico, out_ico_desktop, proj_root)
