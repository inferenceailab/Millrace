import { defineConfig } from 'vite'

// One self-contained ES module: a host drops in a <script type="module"> and gets the element.
// That is the shape the shipping packages would use too — an embedded resource, no CDN.
export default defineConfig({
  build: {
    lib: {
      entry: 'src/millrace-jobs.ts',
      formats: ['es'],
      fileName: () => 'millrace-jobs.js',
    },
    outDir: 'dist',
    emptyOutDir: true,
  },
})
