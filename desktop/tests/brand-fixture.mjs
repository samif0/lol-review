// Page scripts are evaluated in a VM with their imports stripped, so the names they
// import from ../ui/brand.mjs are declared in front of the source instead. The
// declaration stays on the first line: line numbers in failures still match the file.
import * as brand from '../ui/brand.mjs';

export const withBrand = source => `const { ${Object.keys(brand).join(', ')} } = ${JSON.stringify(brand)}; ${source}`;
