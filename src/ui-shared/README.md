# Shared UI assets

`millrace.css` is the dashboard's stylesheet, used by **every** official UI package.

§11.4 committed to three UIs over one contract — "features are designed once in the contract,
rendered three times". A per-UI copy of the stylesheet would break that on the presentation side:
three copies drift, and the React dashboard and the Angular dashboard stop looking like the same
product. There is nothing framework-specific in it, so there is nothing to copy.

It is plain CSS with no build step, imported by relative path:

- React — `import '../../../ui-shared/millrace.css'` in `main.tsx`
- Angular — `@import '../../../ui-shared/millrace.css'` in `src/styles.css`

Class names are therefore part of a contract between the UIs. Changing one means checking both.
