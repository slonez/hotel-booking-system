-- One database and one user per service. Services cannot connect to each other's databases.
CREATE USER booking WITH PASSWORD 'booking';
CREATE DATABASE booking OWNER booking;
REVOKE ALL ON DATABASE booking FROM PUBLIC;

CREATE USER payment WITH PASSWORD 'payment';
CREATE DATABASE payment OWNER payment;
REVOKE ALL ON DATABASE payment FROM PUBLIC;

CREATE USER notification WITH PASSWORD 'notification';
CREATE DATABASE notification OWNER notification;
REVOKE ALL ON DATABASE notification FROM PUBLIC;
