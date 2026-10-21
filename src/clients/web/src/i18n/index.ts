import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import account from './en/account.json';
import analysis from './en/analysis.json';
import club from './en/club.json';
import email from './en/email.json';
import common from './en/common.json';
import passwordRecovery from './en/passwordRecovery.json';
import shell from './en/shell.json';
import seasons from './en/seasons.json';
import members from './en/members.json';
import invitations from './en/invitations.json';
import lineage from './en/lineage.json';
import matches from './en/matches.json';
import managers from './en/managers.json';
import session from './en/session.json';
import teams from './en/teams.json';
import triage from './en/triage.json';
import uploads from './en/uploads.json';
import registry from './en/registry.json';

void i18n.use(initReactI18next).init({
  lng: 'en',
  fallbackLng: 'en',
  defaultNS: 'common',
  ns: ['common', 'session', 'account', 'shell', 'club', 'seasons', 'teams', 'members', 'managers', 'matches', 'uploads', 'lineage', 'triage', 'email', 'registry', 'passwordRecovery', 'analysis', 'invitations'],
  resources: { en: { common, session, account, shell, club, seasons, teams, members, managers, matches, uploads, lineage, triage, email, registry, passwordRecovery, analysis, invitations } },
  interpolation: { escapeValue: false },
});

export default i18n;
