using System.Collections.Generic;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

/// <summary>
/// Herramienta de editor: pone el pivot de TODOS los sprites de las texturas seleccionadas en
/// "Bottom Center", conservando los sprites existentes (no rompe los clips de animación).
///
/// Uso: guarda este archivo dentro de una carpeta llamada "Editor" (Assets/Editor),
/// selecciona en el Project las imágenes (sprite sheets) y usa el menú
/// Tools > Sprites > Pivot a Bottom Center.
/// Requiere el paquete "2D Sprite" (viene incluido en las plantillas 2D).
/// </summary>
public static class SetSpritePivotBottom
{
    [MenuItem("Tools/Sprites/Pivot a Bottom Center (texturas seleccionadas)")]
    private static void Apply()
    {
        var paths = new HashSet<string>();
        foreach (Object obj in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        int textures = 0, sprites = 0;
        foreach (string path in paths)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;

            if (importer.spriteImportMode == SpriteImportMode.Multiple)
            {
                var factory = new SpriteDataProviderFactories();
                factory.Init();
                var provider = factory.GetSpriteEditorDataProviderFromObject(importer);
                provider.InitSpriteEditorDataProvider();

                SpriteRect[] rects = provider.GetSpriteRects();
                foreach (SpriteRect r in rects)
                {
                    r.alignment = SpriteAlignment.BottomCenter;
                    r.pivot = new Vector2(0.5f, 0f);
                }
                provider.SetSpriteRects(rects);
                provider.Apply();
                sprites += rects.Length;
            }
            else if (importer.spriteImportMode == SpriteImportMode.Single)
            {
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteAlignment = (int)SpriteAlignment.BottomCenter;
                importer.SetTextureSettings(settings);
                sprites += 1;
            }
            else continue;

            importer.SaveAndReimport();
            textures++;
        }

        Debug.Log($"[SetSpritePivotBottom] Pivot cambiado a Bottom Center en {sprites} sprites de {textures} texturas.");
    }
}
