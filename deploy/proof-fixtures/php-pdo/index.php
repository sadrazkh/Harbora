<?php
// Connects the plainest way there is, through four variables, and says what happened. Never its password.
header('Content-Type: application/json');

$host = getenv('DB_HOST') ?: '';
$answer = ['dbHost' => $host, 'drivers' => class_exists('PDO') ? PDO::getAvailableDrivers() : []];

try {
    $pdo = new PDO(
        'mysql:host=' . $host . ';dbname=' . (getenv('DB_NAME') ?: ''),
        getenv('DB_USER') ?: '',
        getenv('DB_PASSWORD') ?: '',
        [PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION, PDO::ATTR_TIMEOUT => 5]
    );
    $answer['ok'] = true;
    $answer['one'] = (int) $pdo->query('SELECT 1')->fetchColumn();
} catch (Throwable $error) {
    $answer['ok'] = false;
    $answer['error'] = $error->getMessage();
}

echo json_encode($answer);
