import Link from '@docusaurus/Link';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import clsx from 'clsx';

import styles from './index.module.css';

const sections = [
  {
    title: 'Onboarding',
    description: 'Start here for installation, first run, and initial setup.',
    href: '/docs/category/onboarding',
  },
  {
    title: 'Guides',
    description: 'Task-driven walkthroughs for common workflows.',
    href: '/docs/category/guides',
  },
  {
    title: 'Architecture',
    description: 'System-level design and key ADRs.',
    href: '/docs/category/architecture',
  },
  {
    title: 'Deployment',
    description: 'Packaging and deployment documentation.',
    href: '/docs/category/deployment',
  },
  {
    title: 'Reference',
    description: 'Protocol, contracts, and interfaces.',
    href: '/docs/category/reference',
  },
  {
    title: 'Releases',
    description: 'Roadmap and release process artifacts.',
    href: '/docs/category/releases',
  },
];

export default function Home(): JSX.Element {
  return (
    <Layout
      title="BBS Documentation"
      description="Documentation hub for Battle Bot Stadium"
    >
      <header className={styles.heroBanner}>
        <div className="container">
          <div className={styles.heroContent}>
            <div className={styles.heroLogoWrap}>
              <img
                src="/img/BBS_Logo_Final.png"
                alt="Battle Bot Stadium logo"
                className={styles.heroLogo}
              />
            </div>
            <div className={styles.heroTextWrap}>
              <Heading as="h1" className={styles.heroTitle}>
                BBS Documentation
              </Heading>
              <p className={styles.heroSubtitle}>
                User-facing docs, operational guidance, and technical reference.
              </p>
              <div className={styles.heroCtaRow}>
                <Link className="button button--primary button--lg" to="/docs">
                  Open Docs
                </Link>
                <Link className="button button--secondary button--lg" to="/docs/category/onboarding">
                  Start Onboarding
                </Link>
              </div>
            </div>
          </div>
        </div>
      </header>

      <main className="container margin-vert--lg">
        <section className={styles.grid}>
          {sections.map((section) => (
            <article key={section.title} className={clsx('card', styles.card)}>
              <div className="card__header">
                <Heading as="h2">{section.title}</Heading>
              </div>
              <div className="card__body">
                <p>{section.description}</p>
              </div>
              <div className="card__footer">
                <Link to={section.href}>Browse {section.title}</Link>
              </div>
            </article>
          ))}
        </section>
      </main>
    </Layout>
  );
}
