import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import account from './en/account.json';
import common from './en/common.json';
import session from './en/session.json';

void i18n.use(initReactI18next).init({
  lng: 'en',
  fallbackLng: 'en',
  defaultNS: 'common',
  ns: ['common', 'session', 'account'],
  resources: { en: { common, session, account } },
  interpolation: { escapeValue: false },
});

export default i18n;
