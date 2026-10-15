import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import account from './en/account.json';
import club from './en/club.json';
import common from './en/common.json';
import shell from './en/shell.json';
import seasons from './en/seasons.json';
import members from './en/members.json';
import managers from './en/managers.json';
import session from './en/session.json';
import teams from './en/teams.json';

void i18n.use(initReactI18next).init({
  lng: 'en',
  fallbackLng: 'en',
  defaultNS: 'common',
  ns: ['common', 'session', 'account', 'shell', 'club', 'seasons', 'teams', 'members', 'managers'],
  resources: { en: { common, session, account, shell, club, seasons, teams, members, managers } },
  interpolation: { escapeValue: false },
});

export default i18n;
