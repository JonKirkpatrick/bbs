import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const algoliaAppId = process.env.ALGOLIA_APP_ID;
const algoliaApiKey = process.env.ALGOLIA_SEARCH_API_KEY;
const algoliaIndexName = process.env.ALGOLIA_INDEX_NAME;
const algoliaEnabled = Boolean(algoliaAppId && algoliaApiKey && algoliaIndexName);

const config: Config = {
  title: 'BBS Documentation',
  tagline: 'Battle Bot Stadium docs for users, operators, and contributors',
  favicon: 'img/bbs.ico',

  url: 'https://jonkirkpatrick.github.io',
  baseUrl: '/bbs/',

  organizationName: 'JonKirkpatrick',
  projectName: 'bbs',

  onBrokenLinks: 'warn',
  markdown: {
    hooks: {
      onBrokenMarkdownLinks: 'warn',
    },
  },

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      {
        docs: {
          path: '../docs',
          routeBasePath: 'docs',
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/JonKirkpatrick/bbs/tree/main/',
          showLastUpdateAuthor: true,
          showLastUpdateTime: true,
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    navbar: {
      logo: {
        alt: 'BBS logo',
        src: 'img/icon-32.png',
      },
      title: 'BBS Docs',
      items: [
        {to: '/docs', label: 'Docs', position: 'left'},
        {to: '/docs/category/onboarding', label: 'Onboarding', position: 'left'},
        {to: '/docs/category/guides', label: 'Guides', position: 'left'},
        {
          href: 'https://github.com/JonKirkpatrick/bbs',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            {label: 'Overview', to: '/docs'},
            {label: 'Reference', to: '/docs/category/reference'},
            {label: 'Releases', to: '/docs/category/releases'},
          ],
        },
        {
          title: 'Project',
          items: [
            {
              label: 'Repository',
              href: 'https://github.com/JonKirkpatrick/bbs',
            },
            {
              label: 'Contributing',
              href: 'https://github.com/JonKirkpatrick/bbs/blob/main/CONTRIBUTING.md',
            },
          ],
        },
      ],
      copyright: `Copyright ${new Date().getFullYear()} BBS contributors.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
    },
    ...(algoliaEnabled
      ? {
          algolia: {
            appId: algoliaAppId!,
            apiKey: algoliaApiKey!,
            indexName: algoliaIndexName!,
            contextualSearch: true,
            searchPagePath: 'search',
          },
        }
      : {}),
  } satisfies Preset.ThemeConfig,
};

export default config;
