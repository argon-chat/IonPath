/// <reference types="vitest" />
import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import dts from 'vite-plugin-dts'
import camelCase from 'camelcase'
import packageJson from './package.json'

const packageName = packageJson.name.split('/').pop() || packageJson.name

export default defineConfig({
  build: {
    lib: {
      entry: resolve(__dirname, 'src/index.ts'),
      formats: ['es', 'cjs', 'umd', 'iife'],
      name: camelCase(packageName, { pascalCase: true }),
      fileName: packageName,
    },
  },
  plugins: [
    // `bundleTypes` is what `rollupTypes` was called before vite-plugin-dts 5 moved onto
    // unplugin-dts. The old name is silently ignored, which drops the rollup and emits a tree of
    // per-file `.d.ts` instead of the single `dist/ion.webcore.d.ts` that `package.json` points
    // `types` at — a published package with no types and no error to say so.
    dts({ bundleTypes: true }),
  ],
  test: {},
})
