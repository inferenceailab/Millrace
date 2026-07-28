// TypeScript 7 requires a declaration for a side-effect import of a non-code asset, where 5.x
// accepted it silently (TS2882). Vite resolves the CSS at build time — this only tells the type
// checker the import is legitimate, and it carries no value.
declare module '*.css';
