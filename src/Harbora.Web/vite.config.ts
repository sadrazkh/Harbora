import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'node:path';

// Vite builds the Vue islands + Tailwind CSS straight into wwwroot/build with a manifest.
// Razor reads that manifest (ViteManifest.cs) to reference the hashed assets. This is the
// whole "embedded Vue" story — no standalone Node server runs in production.
export default defineConfig({
  plugins: [vue()],
  base: '/build/',
  build: {
    // Write the manifest at build/manifest.json (NOT the default .vite/ hidden dir):
    // the .NET SDK excludes dot-folders from `dotnet publish`, which silently dropped the
    // manifest in the Docker image and left the app with no CSS/JS links.
    manifest: 'manifest.json',
    outDir: resolve(__dirname, 'wwwroot/build'),
    emptyOutDir: true,
    rollupOptions: {
      // Relative input → stable manifest key "Scripts/main.ts" (matches ViteManifest.Resolve).
      input: 'Scripts/main.ts',
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    cors: true,
  },
});
