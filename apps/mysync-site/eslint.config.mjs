import nx from '@nx/eslint-plugin'
import baseConfig from '../../eslint.config.mjs'

export default [
  ...nx.configs['flat/react'],
  ...baseConfig,
  // public/ holds data fixtures only (site-data.large.js is 20 MB); there is no code to lint.
  { ignores: ['public/**'] },
  {
    files: ['**/*.ts', '**/*.tsx', '**/*.js', '**/*.jsx'],
    rules: {
      // Every file name in a publication is untrusted input.
      'react/no-danger': 'error',
      'no-restricted-syntax': ['error', { selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']", message: 'Never render file names as HTML.' }],
    },
  },
]
